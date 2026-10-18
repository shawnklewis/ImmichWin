# ImmichWin — design

Native Windows desktop client (WinUI 3 / Windows App SDK, C#, .NET) for the user's self-hosted Immich server. It is the user's own client, not an official Immich app.

## Intent
View and upload photos from the user's Immich server without a browser; keep a local cache for moderate offline viewing; and run an automatic Claude-CLI pipeline that tags/albums photos and "develops" RAW files into JPEGs, including while the user is away from the PC.

## Environment (given)
- Server: http://localhost:2283 (public: https://photos.kurtnelle.com via cloudflared). ~6,270 assets. Don't touch D:\Immich or the Cloudflare tunnel.
- Auth: Immich API key via `x-api-key`. The user supplies a fresh key at development time. Never hard-code or commit it. Stored at runtime in Windows Credential Locker.
- Repo: new git repo at C:\Users\shawn\source\repos\ImmichWin. Package as MSIX (Store-submittable later).
- SDKs present: .NET 9 and 10, Visual Studio 2022.

## Goals (MVP)
1. Sign-in: server URL + API key, validated against the server; key in Credential Locker.
2. Timeline grid with thumbnails (`POST /api/search/metadata`, `GET /api/assets/{id}/thumbnail`), virtualized; full-size viewer (original/preview).
3. Albums list and album view (`GET /api/albums`).
4. Upload: selected files (`POST /api/assets` multipart) and a watched-folder uploader; checksum dedupe via `POST /api/assets/bulk-upload-check`.
5. Offline cache: thumbnails and any previewed/opened asset stored on disk under a user-set size cap (LRU eviction). When the server is unreachable the app browses cached items only and says so.
6. Claude pipeline (see below).
7. Short README. Verified: builds, signs in, shows timeline with thumbnails, opens photos, uploads a test file against the local server.

## Claude pipeline
- Runs the `claude` CLI non-interactively (`claude -p`) per image (or batch), fed a cached preview/original path and a prompt; expects structured JSON back: tags, album suggestions, and optionally a "develop" request.
- Output applied to Immich via API: create/assign tags (`/api/tags`), add to albums (existing, or create new).
- **Mode: automatic by default**, manual (user picks photos, triggers run) also available. Automatic runs on newly arrived/uploaded assets, including when the user is away; a background worker/queue, resumable, with retry.
- **Apply mode: changes go straight into Immich by default**; a setting switches to a review queue instead.
- **RAW development:** for some photos the pipeline invokes Claude with the Affinity MCP workflow to produce a developed JPEG from the RAW. The JPEG is uploaded to Immich as a new asset; the RAW original is never modified or deleted. (Builder's call: link them by putting both in an auto-created album/tag, since Immich has stack support to consider.) Same direct-apply default.
- Activity log of every automated change with per-item undo (remove the tags/album membership it added, delete only assets the pipeline itself created).
- The Claude command line, prompt templates, and Affinity step are configurable settings, not hard-coded.

## Non-goals
- Not an official Immich app; no Immich branding claims.
- No Flutter/PWA; no mobile.
- No full offline mirror of the 6,270 assets; no offline editing/sync of changes made while offline (MVP is read-only offline).
- No server admin features, user management, sharing, face/people management in MVP.

## Anti-goals
- Pipeline must never delete or overwrite user originals.
- Never leak the API key (code, logs, git, README).
- App must not hang or crash when the server is unreachable.

## Constraints and notes
- Affinity MCP is configured on a **different machine**, not this one. This machine's build must make the Affinity step pluggable/optional, and must not be blocked on or tested against it now (user said not to test it first). The tag/album classification part must work on this machine with plain `claude -p`.
- Claude CLI availability/auth on this machine is assumed; the app detects absence and reports it.
- Pipeline output is untrusted text: validate JSON, cap tag counts/lengths, never execute anything from it.

## Left to the builder
Project layout, MVVM/DI libraries, cache storage format (files + SQLite index suggested), queue persistence, thumbnail virtualization approach, UI styling, test strategy, MSIX signing for local dev.

## Decisions made by the builder (not the user)
- WinUI 3 on current stable Windows App SDK; target .NET 9 unless WinUI tooling demands otherwise.
- Cache size default and eviction (LRU) are defaults the user can change.

## Builder-check resolutions (my calls unless noted; user may override)
- Image input to `claude -p`: image path in the prompt with only the Read tool allowed (`--allowedTools Read`), run with cwd set to the cache folder; JSON schema `{tags:[str], albums:[str], develop:bool, notes:str}` defined by the builder. Tags namespaced `ai/…`; max 10 tags, 40 chars each.
- Albums: match existing names case-insensitively first; new albums prefixed/limited (max 3 new per run). Undo only removes tag/album links the pipeline recorded in its own log, never user-made ones.
- RAW develop: configurable command template, `{input}` path in, `{output}` path out, run by this app; "develop" applies to RAW assets that Claude flags (setting: RAW-only vs Claude decides). Unconfigured = skipped. Developed JPEGs are tagged `ai/developed` and excluded from the pipeline and the uploader (no loops).
- Watched folder: multiple, recursive, never deletes or moves local files; remembers uploaded checksums.
- Prefer the localhost URL, fall back to the public URL if configured. Cache not encrypted in MVP (protected by user profile ACLs). Unpackaged dev run is fine; MSIX packaging produced separately with a self-signed dev cert.
- Verification: uploads one small test image and removes it afterward; the pipeline is verified against that test asset only (dry run on real assets not done).

## End-to-end objective (user, latest)
Camera photo -> auto-uploaded to Immich by a SEPARATE pipeline -> this Windows app notices the new asset and calls claude-cli on it. So: the app is the trigger; it polls the server for new assets (any uploader), runs resident in the system tray, and classifies NEW assets only by default (backfill = manual button, daily cap setting). Resolves builder-check Q1/Q2.
