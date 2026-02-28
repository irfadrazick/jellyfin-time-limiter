# Project Journal — Jellyfin Time Limiter Plugin

---

## 2026-02-28 — Initial Implementation

### Context / Goal
Build a Jellyfin plugin that enforces per-user daily playback time limits.

### Key Decisions Made
- **Admin users**: always exempt, no tracking at all
- **Warning**: send a Jellyfin client message at exactly 5 minutes remaining (once per session, based on *completed/persisted* seconds not total)
- **Limit config**: global default (minutes) + per-user overrides
  - Per-user `DailyLimitMinutes`: `-1` = use global default, `0` = no limit, `>0` = specific limit
- **Enforcement**: block *new* playback when `completedSeconds >= limitSeconds`; any session already playing when limit is crossed is allowed to finish
- **Data storage**: JSON file at `{AppPaths.DataPath}/TimeLimiter/playtime.json` (no database)
- **No dedicated API controller for v1** was the original plan, but a minimal controller was added for the Reset action needed by the config UI

### Architecture
```
Plugin.cs                      — BasePlugin<PluginConfiguration>, IHasWebPages, static Instance
PluginServiceRegistrar.cs      — IPluginServiceRegistrator, registers singleton services
Configuration/PluginConfiguration.cs  — DefaultDailyLimitMinutes (int), UserLimits (UserLimitEntry[])
Models/PlaytimeData.cs         — Dictionary<string, Dictionary<string, long>>  userId->date->seconds
Services/PlaytimeTrackerService.cs    — all state + persistence logic
HostedServices/PlaytimeHostedService.cs — event wiring + 60s warning timer
Api/TimeLimiterController.cs   — GET /TimeLimiter/Status, POST /TimeLimiter/Reset/{userId}
Configuration/configPage.html  — embedded admin UI (EmbeddedResource)
```

### Implementation Details
- **PlaytimeTrackerService** holds:
  - `_data` (persisted records)
  - `_activeSessions`: `sessionId → (UserId, StartTime)`
  - `_warnedSessions`: sessions that already got the 5-min warning
  - All methods thread-safe via `private readonly object _lock`
- `IsOverLimit` checks *completed* (persisted) seconds only — active sessions are not counted toward blocking
- `IsNearLimit` also checks *completed* seconds only — so the warning fires based on persisted time
- `StopSession` adds elapsed time to persisted total and removes from active sessions
- **PlaytimeHostedService** subscribes to `ISessionManager.PlaybackStart` / `PlaybackStopped`
- Warning timer uses `PeriodicTimer` (every 60s), cancelled on `StopAsync`
- `PlaybackStart` fires `OnPlaybackStart(PlaybackProgressEventArgs)`, `PlaybackStopped` fires `OnPlaybackStopped(PlaybackStopEventArgs)`
- Session stopping uses `ISessionManager.SendPlaystateCommand(..., PlaystateCommand.Stop, ...)`
- Warning message uses `ISessionManager.SendMessageCommand(..., new MessageCommand { Header, Text, TimeoutMs=10000 }, ...)`

### Bugs / Gotchas Fixed
- `PermissionKind` namespace is `Jellyfin.Data.Enums.PermissionKind`, NOT `MediaBrowser.Model.Users.PermissionKind`
  - Affects: `PlaytimeHostedService.cs` and `Api/TimeLimiterController.cs`

### Project Config
- Plugin GUID: `a8b3c2d1-e4f5-6789-abcd-ef0123456789`
- Target: `net9.0`
- NuGet refs: `Jellyfin.Controller 10.9.11`, `Jellyfin.Model 10.9.11` (both `ExcludeAssets=runtime`)
- `Microsoft.Extensions.Hosting.Abstractions 9.0.0` (ExcludeAssets=runtime)

### Status
- All files written and **compiled successfully** (0 errors, 0 warnings)
- .NET 9 SDK installed via Homebrew (`brew install dotnet@9`)
- Next step: deploy to Jellyfin and functional test

### Build / Deploy
```sh
export PATH="/opt/homebrew/opt/dotnet@9/bin:$PATH"
dotnet build
mkdir -p ~/.local/share/jellyfin/plugins/TimeLimiter_1.0.0.0
cp bin/Debug/net9.0/Jellyfin.Plugin.TimeLimiter.dll ~/.local/share/jellyfin/plugins/TimeLimiter_1.0.0.0/
# restart Jellyfin
```

---

## 2026-02-28 — Build Fixes

### Problems Found During Compilation
1. **`IPluginServiceRegistrator` not found** — wrong namespace and wrong method signature
   - Correct namespace: `MediaBrowser.Controller.Plugins` (NOT `MediaBrowser.Common.Plugins`)
   - Correct signature: `RegisterServices(IServiceCollection, IServerApplicationHost)` (NOT `IServiceProvider`)
   - Fix: updated `PluginServiceRegistrar.cs` with correct using + parameter type
   - Source of truth: `MediaBrowser.Controller.xml` docs in `~/.nuget/packages/jellyfin.controller/10.9.11/`

2. **Missing NuGet reference** — `Jellyfin.Common` package needed explicitly
   - `MediaBrowser.Common.dll` is in `Jellyfin.Common` NuGet package (separate from `Jellyfin.Controller`)
   - Added `<PackageReference Include="Jellyfin.Common" Version="10.9.11" ExcludeAssets="runtime" />`

### Final Build Result
```
Build succeeded.  0 Warning(s)  0 Error(s)
```

---

## 2026-02-28 — Deploy & First Successful Load

### Deploy Steps
1. Built release DLL: `dotnet build -c Release`
2. Copied to server: `scp bin/Release/net9.0/Jellyfin.Plugin.TimeLimiter.dll root@jellyfin:/var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0/`
3. First restart failed — `Permission denied` on `meta.json`
   - Root cause: `scp` as root creates files owned by root; Jellyfin runs as `jellyfin` user
   - Fix: `chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0`
4. Second restart succeeded:
   ```
   [INF] Loaded plugin: Time Limiter 1.0.0.0
   [INF] TimeLimiter: PlaytimeHostedService started.
   ```

### Confirmed Working
- Plugin loads and appears in Jellyfin dashboard
- `PlaytimeHostedService` starts (event subscriptions + warning timer active)
- Config page renders correctly (global limit + per-user table with used/remaining/reset)

---

## 2026-02-28 — Version Mismatch Fixes + UI Bugs

### Root Cause: Wrong Jellyfin Package Version
- Server is **Jellyfin 10.11.5** but plugin was originally compiled against **10.9.11**
- `Jellyfin.Data.Entities.User` moved to `Jellyfin.Database.Implementations.Entities.User`
- `HasPermission(PermissionKind)` became an extension method: `Jellyfin.Data.UserEntityExtensions.HasPermission(IHasPermissions, Jellyfin.Database.Implementations.Enums.PermissionKind)`
- Must compile against **exactly 10.11.5** — compiling against 10.11.6 also fails because the server provides `MediaBrowser.Controller 10.11.5.0` not `10.11.6.0`

### Changes Made
- **`.csproj`**: all Jellyfin packages bumped to `10.11.5`; added `Jellyfin.Data 10.11.5`
- **`PlaytimeHostedService.cs`** + **`Api/TimeLimiterController.cs`**: replaced `using Jellyfin.Data.Enums` with `using Jellyfin.Data` + `using Jellyfin.Database.Implementations.Enums`

### Jellyfin Plugin Disable Gotcha
When a plugin fails to load, Jellyfin sets `"status": "NotSupported"` in the plugin's `meta.json`.
The plugin stays disabled on subsequent restarts even if the DLL is replaced.
Fix: `sed -i 's/"status": "NotSupported"/"status": "Active"/' /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0/meta.json`

### UI Bug Fixes
1. **"Remaining: Unlimited" misleading for -1 limit** — was because the Status API returned 500 (TypeLoadException), JS fell back to `[]`, and `formatMinutes(-1)` = "Unlimited". Fixed by:
   - Fixing the API (version mismatch) so it returns real data
   - JS logic: only show "Unlimited" when `status.LimitSeconds === 0`; show "–" if status call failed
2. **Used Today not updating** — config page loaded data once on open. Fixed by:
   - `setInterval(refreshStatus, 30000)` — refreshes only the Used Today / Remaining cells every 30s
   - Clears interval on `pagehide` to avoid memory leaks
   - Reset button now calls `refreshStatus()` immediately after reset

---

## 2026-02-28 — Session Time Persistence on Restart

### Problem
Active sessions were tracked only in memory (`_activeSessions`). On server restart or crash, any in-progress session's elapsed time was lost and never added to `playtime.json`.

### Fix: `FlushActiveSessions()` in `PlaytimeTrackerService`
- Iterates all active sessions, computes elapsed time, adds to persisted totals
- Resets each session's `StartTime` to `now` so elapsed doesn't get double-counted on the next flush
- Calls `Save()` to write to disk

### Called in two places (`PlaytimeHostedService`)
1. **`StopAsync`** — graceful shutdown: all active session time saved before process exits
2. **60s timer loop** — periodic checkpoint: crash/kill loses at most ~60 seconds of time

### Sidebar Entry Fix
To appear in the sidebar (like Playback Reporting), `PluginPageInfo` in `Plugin.cs` needs:
```csharp
EnableInMainMenu = true,
MenuSection = "server",
MenuIcon = "schedule",   // any Material Design icon name
DisplayName = "Time Limiter"
```
Without these, the plugin only appears under Dashboard → Plugins → Settings, not in the sidebar.

### Deploy Command (final, correct)
```sh
ssh root@jellyfin "mkdir -p /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0"
scp bin/Release/net9.0/Jellyfin.Plugin.TimeLimiter.dll root@jellyfin:/var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0/
ssh root@jellyfin "chown -R jellyfin:jellyfin /var/lib/jellyfin/plugins/TimeLimiter_1.0.0.0 && systemctl restart jellyfin"
```
