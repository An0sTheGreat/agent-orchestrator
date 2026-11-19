# Remote image generation model — design (revised: OpenRouter only)

## Intent and why

The Images page (commits `8493e04`, `be1c170`, `4553ab1`, `42a5774`) only generates with a local
model via stable-diffusion.cpp. The user wants to generate with an OpenRouter image model instead
— the same remote API this app already uses everywhere else — picked from a list that shows only
OpenRouter's image-generation-capable models, with the same provider-pinning the text-model picker
already offers.

**Revision note:** an earlier version of this doc proposed a generic "any remote service" design
(OpenAI, Together, Fireworks, etc., via a guessed `/images/generations` shape). The user corrected
that: OpenRouter only, reusing the app's existing OpenRouter integration, and pointed at a
"definer" for image-capable models that turned out to be real. Verified live against OpenRouter's
API before writing this revision (not guessed):

- `GET https://openrouter.ai/api/v1/images/models` — dedicated list of image-generation models
  only (confirmed live: 70 models, each `{id, name, description, created, architecture:
  {input_modalities, output_modalities}, supported_parameters, supports_streaming, endpoints}`).
- `GET https://openrouter.ai/api/v1/images/models/{id}/endpoints` — that model's providers.
  Confirmed live for `bytedance-seed/seedream-4.5`: `{id, endpoints: [{provider_name,
  provider_slug, provider_tag, supported_parameters, allowed_passthrough_parameters,
  supports_streaming, pricing: [{billable, unit, cost_usd}, ...]}]}` — **not** the same shape as
  the text-model endpoints response (`RemoteCatalog.ProviderRow`): priced per image
  (`billable: "output_image"`, `unit: "image"`, `cost_usd`), no `latency_last_30m` /
  `throughput_last_30m` / `uptime_last_30m` fields.
- `POST https://openrouter.ai/api/v1/images` — generates. Request: `model`, `prompt` (required);
  optional `n` (1-10), `aspect_ratio` (or `size`/`resolution`), `output_format`, `response_format`
  (`b64_json` default, or `url`), `provider` (`{order, allow_fallbacks, only, ignore, sort,
  data_collection}` — the **same shape** the app's text-model provider pinning already builds,
  e.g. the existing `{"order":["relace/fp4","open-inference/fp4"],"allow_fallbacks":false}`
  pattern). Response: `{created, data: [{b64_json | url, media_type}], usage, expires_at?}`.
  Errors: `{error: {code, message, metadata?}}`.

Confirmed via OpenRouter's own docs (`openrouter.ai/docs/api/api-reference/images/generate-an-image`)
and a live request to both GET endpoints above.

## Goals

- From the Images page, pick an OpenRouter image-generation model (filtered to only those —
  `architecture.output_modalities` contains `"image"`) and, optionally, pin one or more providers
  for it, the same way a chat worker already can.
- Generate with it: `POST /api/v1/images`, write the returned image(s) into the existing gallery.
- Reuse `OPENROUTER_API_KEY` (already how every other OpenRouter call in this app authenticates)
  — no new credential concept.

## Non-goals

- No other remote service (OpenAI, Together, etc.) — OpenRouter only.
- No latency/uptime/throughput columns in the image provider picker — that data isn't in the
  image endpoints response; price per image is what's available.
- Not changing the local generation path, gallery, or stage.

## Constraints

- Reuse `_remoteHttp` (`MainWindow.RemoteApi.cs:22`) for the HTTP calls.
- `OPENROUTER_API_KEY` is the fixed key env var — `RemoteCatalog.Services[0]` is already
  OpenRouter (`Id: "openrouter"`, `KeyEnv: "OPENROUTER_API_KEY"`); read the key the same way
  text-model fetches already do (`Environment.GetEnvironmentVariable(service.KeyEnv)`).
- `ImageSettings` round-trips whole through `AppSettings.ImageGen` and each picture's sidecar
  `.json` — new string fields need no extra serialization work.

## Design

### New data (mirrors `RemoteCatalog.cs`'s existing `ModelEntry`/`ParseModels`/`FetchModelsAsync`
and `ProviderRow`/`ParseProviders`/`FetchProvidersAsync`, but OpenRouter-only and image-shaped)

```csharp
// RemoteCatalog.cs additions
public sealed record ImageModelEntry(string Id, string Name);
public sealed record ImageProviderRow(string ProviderName, string ProviderTag, decimal? CostPerImage);

public static List<ImageModelEntry> ParseImageModels(string json);
public static async Task<List<ImageModelEntry>> FetchImageModelsAsync(HttpClient http, string? apiKey, CancellationToken ct);

public static List<ImageProviderRow> ParseImageProviders(string json);
public static async Task<List<ImageProviderRow>> FetchImageProvidersAsync(HttpClient http, string modelId, CancellationToken ct, string? apiKey);
```

`FetchImageModelsAsync` hits `https://openrouter.ai/api/v1/images/models` directly (fixed URL, no
`baseUrl` parameter needed — unlike the text-model fetch, there's only ever one service).
`FetchImageProvidersAsync` hits `https://openrouter.ai/api/v1/images/models/{modelId}/endpoints`.
`ParseImageProviders` reads each endpoint's `pricing` array for the entry where
`billable == "output_image"`, taking its `cost_usd` as `CostPerImage` (`null` if absent).

### `ImageSettings` (`ImageGen.cs`)

```csharp
public string RemoteModel { get; set; } = ""; // "" = local (today's behavior); an OpenRouter image model id otherwise
public string RemoteProvider { get; set; } = ""; // comma-separated provider tags to pin, "" = let OpenRouter choose
```

Two plain strings, not a nullable record — matches this file's own existing sentinel style
(`InitImage: "" = text to image"`) and needs no new type for "local vs remote."

### UI (Images page, `MainWindow.ImageGen.cs`)

Same "Model source" toggle shape as before (mirrors `MainWindow.ModelSettings.cs`'s `SourceButton`
/ `localPanel` / `remotePanel` pattern), but the remote side is now: a searchable model picker
listing `FetchImageModelsAsync`'s results (reuse the existing `ModelPicker` control
(`MainWindow.ModelPicker.cs`) if its API takes a plain list of `(Id, Label)` pairs — check its
signature before deciding whether to reuse it directly or build a simpler `ComboBox`), and a
"Pick providers…" button opening a compact popup listing `FetchImageProvidersAsync`'s rows (name,
tag, price per image, a tick to pin it) — this is a smaller version of
`MainWindow.ApiModel.cs`'s existing `BuildProviderDialog`, reusable in spirit but without the
latency/throughput/uptime columns that endpoint doesn't have.

### Generation (`GenerateImagesAsync`)

Same branch point as before: `s.RemoteModel.Length > 0` skips the local runtime/recipe/sd-cli
block entirely. Build the request body `{model: s.RemoteModel, prompt: s.Prompt, n: s.Batch,
aspect_ratio: s.Aspect, response_format: "b64_json", provider: {order: s.RemoteProvider.Split(','),
allow_fallbacks: false}}` (omit `provider` when `RemoteProvider` is empty — mirrors how the
text-model side omits provider pinning when none is chosen). POST to
`https://openrouter.ai/api/v1/images` via `_remoteHttp`, bearer token from
`OPENROUTER_API_KEY`. Parse `data[]`: `b64_json` (base64-decode) or `url` (download); write each
to the matching `OutputNames` file path; the existing shared tail (rename, sidecar, gallery
refresh) is unchanged and already tolerates fewer files than requested.

## What's left to the builder

- Whether to reuse `MainWindow.ModelPicker.cs`'s existing control for the model list, or build a
  plain `ComboBox` with a text filter — check that file's signature first.
- Exact layout/styling of the new toggle row and provider popup, following this file's existing
  helpers.
- Exact error wording beyond what's specified (missing key, a 4xx/5xx reply, fewer images than
  requested).
