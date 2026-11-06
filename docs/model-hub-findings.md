# Models Hub: findings from Unsloth Studio, and the plan

Researched 2026-11-06 for a "Models Hub" page under Remote API in the launcher. Unsloth Studio is AGPL-3.0:
this records behaviour and public Hugging Face API facts only. Nothing of its code is to be copied.

## How it was investigated

- **REA on the exe** (`Y:\Programs\Unsloth\unsloth-studio.exe`, 38.8 MB, PE32+ x64): it is a Tauri shell with only icon resources.
  The UI is a web front end the shell loads; the HF logic is not in the exe. REA's native providers (Ghidra, IDA) are not configured on
  this machine, so string search and decompilation were unavailable; `inspect_pe_resources` worked. The WebView2 instance was not started
  with a debug port, so `analyze_web_bundle` had nothing to attach to.
- **Source** at `Y:\Programs\.unsloth_source\studio` (frontend `frontend/src/features/hub`, backend `backend/hub`): read for behaviour.
- **Live calls to huggingface.co** (anonymous GETs) confirmed the API facts marked "verified".

## Findings

### Search and sort (HF API, called directly from the front end)
- `GET https://huggingface.co/api/models?search=<q>&sort=<field>&direction=-1&limit=<n>&expand[]=downloads&expand[]=likes&expand[]=lastModified&expand[]=pipeline_tag&expand[]=gguf&expand[]=tags&filter=gguf`. Paging follows the `Link: rel="next"` header.
- Sort options and what is sent (all verified to return 200):

  | UI option | `sort=` |
  |---|---|
  | Latest | `createdAt` (newest 100) plus `trendingScore` (top 100), re-ranked client-side by `(trendingScore+2)/(ageDays+2)^1.6` |
  | Newest | `createdAt` |
  | Trending | `trendingScore` (descending only) |
  | Most downloads | `downloads` |
  | Recently updated | `lastModified` |
  | Most likes | `likes` |

- Empty query: Unsloth shows `author=unsloth` models only. With a query it searches all of HF and floats the first three `unsloth` hits.
- A row's "age" is `lastModified`, even when sorted by Newest. 300 ms debounce. Timeout 15 s. 5 min cache of model details.
- **Search is not fuzzy in Unsloth's Discover tab**: the raw query goes to HF. Verified: HF already treats `qwen 3.8`, `qwen3.8`
  and `qwen-3.8` the same (same top three). Only its On Device tab normalises text (lowercase, strip space/`_`/`.`/`-`, every token must match).
  HF does no typo tolerance, so a real fuzzy search is ours to add (see plan).

### Weights dropdown (GGUF variants)
- Backend lists files with sizes: `GET /api/models/{repo}?blobs=true`, using `siblings[].size` (verified; sizes in bytes).
- Quant label is the substring of the file name matching `UD-`? + `IQn_*` / `Qn_K_*` / `MXFP` / `BF16` / `F16` / `F32`. Shards
  (`-00001-of-00003`) are grouped and their sizes summed. `mmproj` files are not variants (they mark "vision"); drafters, imatrix and
  big-endian files are skipped. Variants sort largest first.
- Each variant shows its size. A coloured info icon says whether it fits, from `required = GiB*1.15 + 1 GiB` against the free VRAM budget
  (97% of the card minus a small reserve): `fits` / `marginal` (fits only the whole card) / `partial` (spills to RAM) / `oom`. With no GPU it
  compares with half the RAM. The list is ordered by fit class, biggest that fits first; the first row is the default.
- "Only show models that fit" works per repo from the smallest quant (size listed, else `params * 0.4` bytes); models already on disk stay.

### Downloads
- Unsloth runs a Python worker per job: `snapshot_download(repo, allow_patterns=<variant's shards + mmproj>)`. Standard HF cache layout.
  Custom folder: files go to `<dir>\hub`, and that folder is registered as a scan folder.
- Header toggle Auto / HTTP / Xet is `HF_HUB_DISABLE_XET`. HTTP resumes from `.incomplete`; Xet restarts from scratch. Cancel kills the worker.
  Progress comes from scanning the cache's `blobs` folder (finished plus `.incomplete` bytes against expected sizes).
- Token: optional; passed as `HF_TOKEN` to the worker; listings retry anonymously if the token is refused.

### Icons
- Unsloth shows its own 15 logos for `unsloth/` repos (by name prefix) and, for other owners, the HF avatar from
  `/api/organizations|users/{name}/overview` (cached 24 h, 6 concurrent), with a coloured-initial tile as fallback.

### Pitfalls to design around
- HF pages are 500 raw rows; filtering client-side shrinks a page, so keep fetching until enough rows survive.
- `trendingScore` rejects ascending order. Repos tagged `gguf` can still be diffusion pipelines. `gguf.total` can be a projector's size.
- Failures back off; HF rate limits anonymous callers, so cache and debounce.

## Our app today (what the hub builds on)
- `AppSettings.ModelsFolder` and `LauncherLogic.HubCache()` already define where models live (HF cache layout, `HF_HUB_CACHE`).
  The engine finds cached GGUFs by repo and quant (`LocalGguf`), so a download into that layout shows up in the model picker unchanged.
- `RemoteVendorIcon` and `Assets/Vendors` (52 logos) already map vendor names to icons: reuse for rows (owner lookup, initial tile fallback).
- The Remote API page (`MainWindow.RemoteApi.cs`, `RemoteApiView`) is the pattern for a sidebar page; its sidebar entry is at `MainWindow.Sidebar.cs:41`.
- No Hugging Face download code exists in the launcher or engine; the engine only reads the cache.

## Plan

**Scope:** a "Models Hub" sidebar page directly under Remote API, GGUF models only in v1.

1. **Page shell** (`MainWindow.ModelHub.cs`, `ModelHubView` beside `RemoteApiView`): left results list + right detail pane, in our theme. Header chips: free VRAM, RAM, CPU, models folder, cache count, and an Auto/HTTP transfer toggle.
2. **Search:** debounced (300 ms) call to `/api/models` with `filter=gguf`. Fuzzy layer on top of HF's own match: HF already ignores spaces and punctuation, so ours adds
   (a) a normalised token match and a small edit-distance score over the fetched rows, to survive typos like `qwn 3.8` (we retry the query with the closest token if HF returns few rows);
   (b) ranking by match quality when sort is "Latest". An `owner/repo` query jumps straight to that repo.
3. **Sort menu** as in your image: Latest, Newest, Trending, Most downloads, Recently updated, Most likes, plus "Only show models that fit". Mapping as in the table above.
4. **Rows:** vendor icon (our `Assets/Vendors`; unknown owners get a coloured-initial tile, no avatar fetch in v1), name, owner, likes, downloads, age.
5. **Detail pane:** HF model card header, README (rendered with our `Markdown.cs`), tags, and the weights dropdown from `?blobs=true` with sizes, shard grouping and a fit marker computed from our own VRAM/RAM readings (same formula).
6. **Downloads:** a C# downloader (no Python): resumable `Range` requests to `https://huggingface.co/{repo}/resolve/main/{file}` into the HF cache layout (`models--org--name\blobs`, `snapshots`, `refs`) so the existing picker and engine see it immediately. Includes the mmproj for vision models, progress, cancel, resume from `.incomplete`, and a simple queue.
   Folder choice: default is `HubCache()` / the models folder setting; "Choose folder" saves to the picked folder and adds it to the models folder scan. Optional HF token read from an environment variable (never stored, same rule as the API key).
7. **Tests:** engine-free C# self-tests for the variant parser (shards, mmproj, UD- labels), the fit class, the fuzzy matcher, the sort-to-query mapping and a fake-HTTP download (resume, cancel). Add a ui-smoke for the page and sort menu. Negative gate for each.

**Not in v1:** On Device tab (the app already lists local models in its picker), safetensors/diffusers downloads, HF avatars, gated-model login UI, a Xet client (HTTP only; Xet falls back to HTTP transparently), ModelScope mirror.

**Open questions**
- v1 GGUF only, or also safetensors?
- Empty search: show Unsloth's models like Unsloth Studio does, or trending GGUF from everyone?
- Default download folder: the HF cache (`HubCache()`, so the picker finds it as it does now) or the "Models folder" setting?
