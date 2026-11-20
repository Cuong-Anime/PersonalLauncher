# PersonalLauncher — Setup & Build Guide

## Prerequisites

- **OS:** Windows 11 x64 (primary target)
- **.NET SDK:** `.NET 10 LTS` (`10.0.404`, pinned in `global.json`)

## Build, Test & Publish

Run from `d:\Github\CuongAnime\PersonalLauncher`:

```powershell
dotnet restore .\PersonalLauncher.slnx --locked-mode
dotnet build .\PersonalLauncher.slnx -c Release --no-restore
dotnet test .\PersonalLauncher.slnx -c Release --no-build
dotnet publish .\src\PersonalLauncher.App\PersonalLauncher.App.csproj -c Release -r win-x64 --self-contained true
```

Or run the packaging script:

```powershell
.\packaging\publish.ps1
```

## Architecture

- `src\PersonalLauncher.Core`: Domain models, contracts, text/path normalization, PIN validation, scan heuristics, authorization & attempt tracking services.
- `src\PersonalLauncher.Infrastructure`: SQLite persistence (`Microsoft.Data.Sqlite`), Argon2id + DPAPI `CurrentUser` passcode & mail credential storage, MailKit TLS SMTP transport + persistent outbox worker, WIC image validation/thumbnailing, Windows Startup (`HKCU\...\Run` + `StartupApproved`), System Tray (`NotifyIcon`), and Single-Instance IPC.
- `src\PersonalLauncher.App`: WPF MVVM desktop shell, 4-language dynamic localization (`vi-VN`, `en-US`, `ja-JP`, `zh-CN`), and custom dark UI using bundled assets from `assets/`.

## User Data Directory

All user data is stored per Windows user under `%LOCALAPPDATA%\PersonalLauncher\`:

```text
%LOCALAPPDATA%\PersonalLauncher\
  library.db
  settings.json
  Assets\games\<game-id>\...
  Cache\thumbnails\...
  Security\passcode
  Security\passcode.bak
  Security\mail-credentials.dat
  Logs\app-yyyyMMdd.log
```
