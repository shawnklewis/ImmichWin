# ImmichWin

An **unofficial**, native Windows (WinUI 3 / .NET) client for a self-hosted [Immich](https://immich.app) server. It is not affiliated with or endorsed by the Immich project.

- Sign in with server URL + API key (key kept in Windows Credential Locker, never in files)
- Timeline grid with thumbnails, full-size viewer (arrow keys / Esc), albums
- Upload selected files, or auto-upload watched folders (SHA-1 dedupe against the server)
- Offline viewing: thumbnails and any photo you open are cached on disk (size-capped, LRU)
- Runs in the system tray; polls the server for **new** photos and runs `claude -p` on each to add tags (`ai/…`) and albums, optionally "developing" RAW files via a command you configure

## Build & run

Requirements: Windows 11, .NET 10 SDK, Windows App SDK 2.5 (restored from NuGet).

```bash
dotnet test                                   # Core logic tests (net9.0)
dotnet build src/ImmichWin.App -p:Platform=x64 -p:WindowsPackageType=None -o out   # unpackaged dev build
out/ImmichWin.App.exe
```

First launch: enter your server URL (default `http://localhost:2283`) and an API key created in Immich → Account Settings → API Keys.
Needed API key permissions: asset read/upload/delete, album read/create/update, tag read/create/update/delete.

## MSIX package

```powershell
./scripts/make-dev-cert.ps1     # creates ImmichWinDev.pfx/.cer (git-ignored)
dotnet publish src/ImmichWin.App -c Release -p:Platform=x64 -r win-x64 -p:GenerateAppxPackageOnBuild=true `
  -p:AppxPackageSigningEnabled=true -p:PackageCertificateThumbprint=<thumbprint> -p:AppxBundle=Never `
  -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageDir=$PWD/AppPackages/
```

To install the sideloaded package, trust `ImmichWinDev.cer` (admin: `Import-Certificate -FilePath ImmichWinDev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople`) and open the `.msix`. For the Microsoft Store, replace the publisher identity in `Package.appxmanifest` with the one Partner Center assigns.

## The Claude pipeline

Camera → (your separate uploader) → Immich → **ImmichWin notices the new asset** → `claude -p` → tags/albums written back.

- Automatic by default; the first poll only records "now", so existing photos are never touched unless you press **Classify existing photos…** on the Activity page.
- Every few minutes (setting) the app lists assets uploaded since the last poll. Daily limit setting protects your Claude usage.
- Claude gets the image path and read-only file access (`--allowedTools Read`), and must reply with JSON `{"tags":[],"albums":[],"develop":false,"notes":""}`. Output is validated and clamped (≤10 tags of ≤40 chars, ≤2 albums, ≤3 new albums per run); tags are prefixed `ai/`.
- Changes apply straight to Immich by default. Turn on *Ask me to approve changes first* to queue them. The **Activity** page lists every change with per-item **Undo** (it removes only the links the pipeline added; originals are never modified or deleted).
- RAW development: if Claude sets `develop` for a RAW file and you've set a *RAW develop command* (e.g. a script that calls your Affinity workflow, using `{input}` and `{output}`), the JPEG is uploaded as a new asset tagged `ai/developed` (excluded from further processing). Unset = skipped.
- The app must be running (it lives in the tray; closing the window hides it, tray → Quit exits).

## Layout

- `src/ImmichWin.Core` – API client, cache, uploader, pipeline (no UI, unit-tested)
- `src/ImmichWin.App` – WinUI 3 app
- `docs/superpowers` – design and plan
