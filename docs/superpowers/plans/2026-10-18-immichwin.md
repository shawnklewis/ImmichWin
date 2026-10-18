# ImmichWin Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** A WinUI 3 desktop Immich client that browses/uploads photos, caches for offline viewing, and (running in the tray) polls the server for new assets and runs `claude -p` on them to apply tags/albums and optionally develop RAWs.

**Architecture:** Two projects: `ImmichWin.Core` (net9.0, no UI: API client, cache, upload, pipeline, SQLite state; fully unit-tested) and `ImmichWin.App` (WinUI 3, MVVM via CommunityToolkit.Mvvm, thin views). One resident process: UI + tray + background `PipelineWorker`.

**Tech Stack:** C#/.NET 9, Windows App SDK (latest stable), CommunityToolkit.Mvvm, Microsoft.Extensions.Hosting/DI, Microsoft.Data.Sqlite, xUnit + a fake `HttpMessageHandler` (WireMock.Net optional), H.NotifyIcon.WinUI (tray), Windows.Security.Credentials.PasswordVault.

**Spec:** `C:\Users\shawn\AppData\Roaming\Claude\side-session-notes\local_0a60f026-fa25-4dbf-88f4-8eab818b8542\immichwin-design.md` (copy into `docs/superpowers/specs/` in Task 1).

## Global Constraints
- Repo: `C:\Users\shawn\source\repos\ImmichWin`, git; never commit an API key. Key only in PasswordVault at runtime; tests use fake keys.
- Auth header `x-api-key`. Default server `http://localhost:2283`; optional fallback `https://photos.kurtnelle.com`.
- Do not touch `D:\Immich` or the cloudflared service.
- Pipeline never deletes/overwrites originals; deletes only assets it created. Developed JPEGs tagged `ai/developed`, excluded from pipeline and uploader. Pipeline tags prefixed `ai/`, ≤10 tags/asset, ≤40 chars each, ≤3 new albums per run.
- Pipeline default: automatic, applies directly (setting `ReviewBeforeApply=false`), new assets only, daily cap setting, backfill only via manual button.
- `claude -p` invoked with image path in prompt, `--allowedTools Read`, cwd = cache folder; command line, prompts, develop command template (`{input}`/`{output}`) are settings. Unset develop command = skip.
- Claude output is untrusted: parse JSON strictly, never execute it.
- Affinity MCP is on another machine; never block on or test it.
- Unreachable server must never hang/crash the app; offline = cached items only, banner shown.
- Final verification: upload one tiny test image, run pipeline on only it, then delete it; no automatic runs on real assets.
- MSIX with self-signed dev cert; unpackaged dev run is acceptable.

## Review Focus
- Server down / DNS fail / 401 key revoked mid-session → banner + cached browsing, no crash (Task 2, 5).
- Asset with `null` dates/EXIF or zero-byte/corrupt file → timeline still renders, upload reports failure (Tasks 3, 6).
- Claude returns malformed JSON, markdown-fenced JSON, 50 tags, or hostile tag text → rejected/clamped, queue continues (Task 8).
- Same asset seen twice by poller / app restarts mid-run → processed once (idempotent state in SQLite) (Task 9).
- Pipeline-created JPEG or tagged asset re-triggers pipeline or watched-folder upload → loop prevented (Tasks 6, 9).
- Cache over size cap while viewing an item → evicts LRU but never the item in view (Task 5).

---

### Task 1: Repo and solution scaffolding
**Files:** Create `ImmichWin.sln`, `src/ImmichWin.Core/`, `src/ImmichWin.App/` (WinUI 3 packaged, `dotnet new` template from Windows App SDK VS templates or hand-written csproj), `tests/ImmichWin.Core.Tests/`, `.gitignore`, `README.md` (stub), `docs/superpowers/specs/…design.md`, `docs/superpowers/plans/…plan.md`.
**Interfaces:** Produces: solution that builds; Core referenced by App and Tests.
- [ ] Step 1: `git init`; create solution/projects; add `.gitignore` (VS + `*.pfx`, `appsettings.local.json`).
- [ ] Step 2: Add a trivial passing test; run `dotnet test` → PASS. Run `msbuild`/`dotnet build` on App for x64 → succeeds. If WinUI tooling needs net8/net10, pick what builds and note it in README.
- [ ] Step 3: Commit.

### Task 2: Immich API client (Core)
**Files:** `Api/ImmichClient.cs`, `Api/Models.cs`, `Api/ConnectionState.cs`; test `Api/ImmichClientTests.cs`.
**Interfaces:** Produces `ImmichClient(HttpClient http, ServerSettings s)` with:
`Task<UserInfo> ValidateAsync(CT)` (GET /api/users/me), `Task<SearchPage> SearchAsync(SearchQuery q, CT)` (POST /api/search/metadata; `SearchPage{Items:IReadOnlyList<AssetDto>, NextPage:string?}`), `Task<Stream> GetThumbnailAsync(string id, ThumbSize size, CT)`, `Task<Stream> GetOriginalAsync(string id, CT)`, `Task<IReadOnlyList<AlbumDto>> GetAlbumsAsync(CT)`, `Task<AlbumDto> GetAlbumAsync(string id, CT)`, `Task<AlbumDto> CreateAlbumAsync(string name, CT)`, `Task AddToAlbumAsync(string albumId, IEnumerable<string> assetIds, CT)`, `Task<IReadOnlyList<TagDto>> GetTagsAsync(CT)`, `Task<TagDto> UpsertTagAsync(string name, CT)`, `Task TagAssetsAsync(string tagId, IEnumerable<string> ids, CT)`, `Task UntagAssetsAsync(...)`, `Task<IReadOnlyDictionary<string,bool>> CheckDuplicatesAsync(IEnumerable<(string clientId,string sha1)>, CT)` (bulk-upload-check), `Task<UploadResult> UploadAsync(UploadRequest r, CT)` (multipart POST /api/assets), `Task DeleteAssetsAsync(IEnumerable<string> ids, CT)`. Typed `ImmichException(Kind: Unreachable|Unauthorized|Server)`.
- [ ] Step 1: Tests with fake handler: header `x-api-key` sent; `ValidateAsync` 401 → `ImmichException{Kind=Unauthorized}`; `HttpRequestException` → `Unreachable`; search deserializes an asset with null `localDateTime`/exif; upload sends multipart fields `deviceAssetId, deviceId, fileCreatedAt, fileModifiedAt, assetData`.
- [ ] Step 2: Run → FAIL. Step 3: Implement. Step 4: Run → PASS. Step 5: Check field names against the live server's `/api/docs` (or openapi json) and adjust DTOs; commit.

### Task 3: Settings and credential storage (Core + App shim)
**Files:** `Settings/AppSettings.cs`, `Settings/ISettingsStore.cs`, `Settings/JsonSettingsStore.cs`, `Security/ISecretStore.cs`, App: `PasswordVaultSecretStore.cs`; tests for JSON store.
**Interfaces:** `AppSettings{ServerUrl, FallbackUrl?, CacheLimitMb=2048, ClaudeCommand="claude", ClassifyPrompt, DevelopCommandTemplate?, ReviewBeforeApply=false, AutoClassify=true, PollMinutes=3, DailyCap=200, WatchFolders:List<string>}`; `ISecretStore{string? Get(); void Set(string); void Clear();}`.
- [ ] Round-trip test (defaults, unknown-field tolerance, corrupt file → defaults); implement; commit. Key never serialized into settings JSON (assert).

### Task 4: Local state database (Core)
**Files:** `Data/StateDb.cs`; test `Data/StateDbTests.cs`.
**Interfaces:** SQLite file in `%LOCALAPPDATA%\ImmichWin\state.db` with tables `cache_items(id,kind,path,bytes,last_access)`, `processed(asset_id PRIMARY KEY, status, at, error)`, `changes(id,asset_id,type[tag|album|asset_created],ref,created_by_pipeline,at,undone)`, `uploaded(sha1 PRIMARY KEY, asset_id, at)`. Methods per table (`TryClaimAsync(assetId)` atomic insert-if-absent for idempotence; `RecordChange`, `ListChanges`, `MarkUndone`).
- [ ] Tests: second `TryClaimAsync` same id returns false; changes persist across reopen. Implement; commit.

### Task 5: Offline cache (Core)
**Files:** `Cache/AssetCache.cs`; test `Cache/AssetCacheTests.cs`.
**Interfaces:** `AssetCache(StateDb db, string root, Func<long> limitBytes)`; `Task<string> GetThumbAsync(string id, ImmichClient c, CT)` / `GetPreviewAsync` / `GetOriginalAsync` return a local path, serve from disk when present, fall back to disk-only when `ImmichException{Unreachable}`; `bool TryGetCached(id, kind, out path)`; `void Pin(id)/Unpin(id)`; LRU eviction after write.
- [ ] Tests: second call doesn't hit network; unreachable + cached → returns path; unreachable + uncached → throws `CacheMissException`; over limit evicts oldest but not pinned item. Implement; commit.

### Task 6: Uploader and watched folders (Core)
**Files:** `Upload/Uploader.cs`, `Upload/FolderWatcher.cs`; tests.
**Interfaces:** `Task<UploadOutcome> UploadFileAsync(string path, CT)` (sha1 → `uploaded` table + bulk-upload-check → skip dup → upload; never modifies/deletes local file; `UploadOutcome{Uploaded|Duplicate|Failed(reason)}`); `FolderWatcher(IEnumerable<string> folders, Uploader u)` recursive, debounces, waits for file to be unlocked/stable, skips files under the pipeline output dir and zero-byte files.
- [ ] Tests: duplicate skipped; zero-byte → Failed; locked-then-released file uploads once; pipeline output dir ignored. Implement; commit.

### Task 7: App shell — sign-in, timeline, viewer, albums
**Files:** App: `Views/SignInPage`, `TimelinePage`, `ViewerPage`, `AlbumsPage`, `AlbumPage`, `SettingsPage`; `ViewModels/*`; `MainWindow.xaml` NavigationView; `App.xaml.cs` DI host.
**Interfaces:** Consumes Tasks 2–6. Timeline: `ItemsRepeater`/`GridView` with incremental-loading `ISupportIncrementalLoading` collection over `SearchAsync` (page size 200, newest first), thumbnails via `AssetCache.GetThumbAsync` loaded lazily on realize. Viewer: preview first, original on demand, left/right keys, pin while open. Banner when `ConnectionState` is offline.
- [ ] Step 1: ViewModel unit tests in Core-free test project only where logic exists (incremental loader paging, offline banner state) — put view-model logic that needs tests in Core (`Timeline/TimelineSource.cs`) with tests: pages append, `NextPage` null ends, error → offline state.
- [ ] Step 2: Implement views. Step 3: Run app against local server (key provided by user via sign-in screen), confirm sign-in, timeline thumbnails, open a photo, albums list/view, offline banner after stopping nothing (simulate by wrong port in settings). Capture screenshot evidence. Commit.

### Task 8: Claude runner and result parsing (Core)
**Files:** `Pipeline/ClaudeRunner.cs`, `Pipeline/ClassificationResult.cs`, `Pipeline/ResultParser.cs`; tests.
**Interfaces:** `IProcessRunner.RunAsync(exe,args,cwd,stdin,timeout,CT) → (int exit,string stdout,string stderr)`; `ClaudeRunner.ClassifyAsync(string imagePath, CT) → ClassificationResult{Tags,Albums,Develop,Notes}`; `ResultParser.Parse(string stdout) → ClassificationResult` (accepts fenced or bare JSON, clamps per Global Constraints, drops non-printable/`/`-containing tags except the `ai/` prefix).
- [ ] Tests: fenced JSON ok; 50 tags → 10; 200-char tag → dropped/truncated to 40; malformed → `ClassificationException`; hostile text (`; rm -rf`, newlines) stays inert data; nonzero exit/timeout → exception. Implement with args `-p <prompt> --allowedTools Read --output-format text`; commit. Detect missing `claude` → clear error.

### Task 9: Pipeline worker, poller, apply and undo (Core)
**Files:** `Pipeline/PipelineWorker.cs`, `Pipeline/Applier.cs`, `Pipeline/Developer.cs`; tests.
**Interfaces:** `PipelineWorker(ImmichClient, AssetCache, ClaudeRunner, Applier, Developer, StateDb, AppSettings)` with `StartAsync/StopAsync`, polls `SearchAsync(createdAfter=lastSeen)` every `PollMinutes`; for each unseen asset not tagged `ai/developed` and not already `processed`: claim → fetch preview into cache → classify → `Applier.ApplyAsync` (tag, album match case-insensitive then create ≤3, records `changes`, or queues for review if `ReviewBeforeApply`) → if `Develop` && RAW && command configured, `Developer.RunAsync` (command template, uploads JPEG via Uploader, tags `ai/developed`, records `asset_created`); respects `DailyCap`; failures recorded and retried ≤3 with backoff; `BackfillAsync(IEnumerable<string> ids)` for manual runs; `UndoAsync(changeId)` removes only recorded links / deletes only `asset_created`.
- [ ] Tests (fakes): same asset in two polls → classified once; restart (new worker, same db) → not reprocessed; asset tagged `ai/developed` skipped; daily cap stops at N; server unreachable → loop continues next tick; undo of a pre-existing user tag leaves it intact; unconfigured develop command → skipped without error. Implement; commit.

### Task 10: Tray, activity log, settings UI
**Files:** App: `TrayIcon` (H.NotifyIcon), close-to-tray, optional start at login (MSIX StartupTask), `ActivityPage` (list changes, per-item Undo, review queue approve/reject, manual "classify selected"/"classify existing" with cap warning), settings controls for every `AppSettings` field, watched-folder list.
- [ ] Implement; manually verify: closing window keeps worker alive; tray Quit stops it; Undo works. Commit.

### Task 11: Packaging and end-to-end verification
**Files:** `Package.appxmanifest`, dev cert script `scripts/make-dev-cert.ps1`, `README.md` (build, run, sign-in, settings, pipeline, privacy, "unofficial client").
- [ ] Step 1: Produce MSIX (x64) with self-signed cert; install and launch.
- [ ] Step 2: E2E with user-supplied key: sign in; timeline; open photo; upload one tiny test image; confirm pipeline picks it up (auto mode), tags appear in Immich; undo; delete test asset. Don't run on real assets.
- [ ] Step 3: Confirm `dotnet test` green and `git status` clean with no secrets (`git grep -i x-api-key` shows only the header name). Commit; hand over.

---
## Self-review notes
Spec coverage: sign-in/Locker (3,7), timeline/viewer/albums (7), upload+watch (6), offline cache (5), Claude pipeline auto/manual/direct-apply/undo/log (8–10), RAW develop pluggable (9), MSIX+README (11). Names are consistent across tasks (`ImmichClient`, `AssetCache`, `StateDb`, `PipelineWorker`).
