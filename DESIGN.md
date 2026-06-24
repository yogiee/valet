# Valet — Design & Build Spec

> **Status:** Spec rev 2 (2026-06-24). Refined after triage of the legacy source repos (`KodiLauncher`, `remote-shutdown-pc`) cloned to `WORKSPACE/`. Scaffolded code at `src/Valet/` builds clean; module implementations not yet written.
> **Target machine:** `igomedia` — Windows 11, AMD, `192.168.69.195`. SSH key `id_ed25519_homelab`, WoL enabled.
> **Dev machine:** `IGOMINIPC` (`192.168.69.197`) on the same LAN — fine for building/server/notify work; lifecycle and `/sleep` behaviors validate on `igomedia` only.

---

## 1. Purpose

Replace three aging single-purpose apps and add one missing capability, all in one Windows 11 tray app:

| Replaces / adds | What it does | Why |
|---|---|---|
| **KodiLauncher** (`baijuxavior/KodiLauncher` — VB.NET+AHK, ~12yr stale) | Launches Kodi on logon/wake, kills on sleep, hands off to Steam Big Picture | Win7/8 cruft (shell replacement, MetroUI, Confluence skin hooks, iMON IR remote) we don't need; modern Kodi doesn't need a watchdog; passive BPM detection beats the manual "Games" tray entry |
| **RemoteShutdown** (`karpach/remote-shutdown-pc` — C#) | HTTP server for sleep/status from HA on port **5009** | Root `/` triggers shutdown instead of being a safe health-check; ships seven actions (shutdown/restart/forceshutdown/hibernate/suspend/turnscreenoff/lock) — we want only suspend. Its modal abort dialog is awful from the couch — replace with tray-balloon countdown |
| **Kodi `script.securitycam` mode:volume hack** | On-screen volume display **inside Kodi only**, driven by HA AVR automation | Doesn't work in Steam / games. OS-level overlay solves this, but **deferred to v1.1** |
| *(new)* `POST /notify` → Windows Toast | Pass-through from HA to Action Center | Lets HA surface alerts on the HTPC screen regardless of foreground app |

### Environment map

| Thing | Identifier |
|---|---|
| AVR | `media_player.rx_v685` (Yamaha RX-V685) |
| HTPC Kodi entity | `media_player.igomedia` |
| HTPC power switch | `switch.igomedia` |
| HTPC host | `igomedia.local` / `192.168.69.195` |
| TV | `media_player.megztv_u7980_tv` |
| AVR source gate for HTPC | `source == "AV1"` |
| Current HA sleep call | `shell_command.suspend_igomedia` → `curl http://igomedia.local:5009/suspend` |

### Consolidation insight

HTPC connects **directly to TV** (audio through AVR, video direct). One process owns Kodi lifecycle, the local HTTP server (sleep/status/notify, later /volume), and eventually the volume OSD. Shared in-memory state (current lifecycle state, last-pushed volume) is the reason this is one app, not three.

---

## 2. Tech stack

**C# / .NET 10, single self-contained `win-x64` exe.** `net10.0-windows10.0.19041.0` (Windows-versioned TFM needed for UWP toast APIs in step 2). WinForms hosts `NotifyIcon`; WPF is reserved for the deferred OSD overlay. `System.Net.HttpListener` for the HTTP server — RemoteShutdown uses Kestrel but the ASP.NET Core dep weight isn't worth it for four routes. Single-file self-contained publish. Inno Setup installer.

The scaffolded layout (`Valet.slnx` + `src/Valet/<module-dirs>` + `installer/`) is already in place and builds clean.

> **Decided against:** Python (poor click-through overlay over fullscreen), Electron/Tauri (too heavy for a tray utility), Rust (Win32 ergonomics not worth it), .NET 8 (.NET 10 is current LTS; the dev box only has .NET 10 ref packs).

---

## 3. High-level architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  Valet.exe  (single process, Task Scheduler logon task)         │
│                                                                  │
│  ┌────────────┐   ┌──────────────────┐   ┌──────────────────┐    │
│  │ Tray /     │   │ Kodi Lifecycle   │   │ Volume OSD       │    │
│  │ NotifyIcon │   │ + Steam Watcher  │   │ (deferred v1.1)  │    │
│  └────────────┘   └────────┬─────────┘   └──────────────────┘    │
│                            │                                     │
│           ┌────────────────┼─────────────────┐                   │
│           │                │                 │                   │
│      ┌────▼─────┐    ┌─────▼─────┐    ┌──────▼──────┐            │
│      │ Config   │    │ Toast     │    │ HTTP Server │  :5009     │
│      │ (JSON)   │    │ (HA→UI)   │    │ HttpListener│            │
│      └──────────┘    └───────────┘    └──────┬──────┘            │
└──────────────────────────────────────────────┼──────────────────┘
                                                │
              ┌─────────────────────────────────┼────────────────┐
              │ Home Assistant                                   │
              │  • rest_command: sleep    → POST /sleep          │
              │  • rest_command: notify   → POST /notify         │
              │  • binary_sensor: GET /                          │
              │  • rest_sensor:   GET /status (activity etc.)    │
              └──────────────────────────────────────────────────┘
```

### State model — reconciliation, not event chains

The lifecycle is a **reconciliation loop**: every ~500 ms (and on power events) we inspect the world and converge:

```
                  ┌── observe ──┐
   ┌──────────────┤              ├──────────────┐
   │ • Steam BPM window present?                │
   │ • Kodi process running?                    │
   │ • Time since boot / time since resume      │
   │ • Current power state                      │
   └──────────────┬──────────────────────────────┘
                  │
   ┌──────────────▼── reconcile ──────────────┐
   │  desired = computeDesired()              │
   │                                           │
   │  if BPM present:        desired = NoKodi │
   │  else if booting:       wait bootDelay    │
   │  else if waking:        wait wakeDelay    │
   │  else:                  desired = Kodi   │
   │                                           │
   │  converge(actual, desired)                │
   └───────────────────────────────────────────┘
```

The `state` label in `/status` (`media` | `gaming` | `booting`) is derived from this snapshot, not stored.

**On sleep (`PowerModeChanged.Suspend`):** synchronously WM_CLOSE Kodi's window (class `Kodi`, fallback `XBMC`), wait up to 2 s, force-kill if still alive, return. Windows force-suspends after a few seconds regardless — cleanup must be fast.

**On wake (`PowerModeChanged.Resume`):** schedule a one-shot reconciliation after `wakeDelaySec`. That reconciliation re-detects BPM and either relaunches Kodi or stays idle.

---

## 4. Module specs

### 4.1 Kodi lifecycle (replaces KodiLauncher)

**Behaviors:**
- **Boot:** at app start (logon task fires), wait `bootDelaySec` (default 4 s) for desktop/session settle, then start the reconciliation loop.
- **Sleep:** kill Kodi gracefully — WM_CLOSE → 2 s timeout → force-kill.
- **Wake:** after `wakeDelaySec`, reconcile; relaunches Kodi if BPM isn't up.
- **Steam BPM handoff:** poll `EnumWindows` for a visible window owned by `steam.exe` whose title contains "Big Picture". When BPM appears → close Kodi. When BPM disappears → reconcile (relaunches Kodi).
- **Graceful close:** WM_CLOSE to window class `Kodi` (Kodi 14+) or `XBMC` (older). If still alive after 5 s, `Process.Kill()`.
- **Single instance:** named mutex `Global\Valet.SingleInstance` (already wired in `Program.cs`).
- **Browse pickers:** Kodi exe and Steam exe both have file pickers in Settings with auto-detected defaults:
  - Kodi: `%LOCALAPPDATA%\Programs\Kodi\kodi.exe`, then `C:\Program Files\Kodi\kodi.exe`.
  - Steam: `C:\Program Files (x86)\Steam\steam.exe`.

**Explicitly dropped from KodiLauncher (do not port):**
- Watchdog / auto-relaunch on crash — modern Kodi doesn't need it.
- Focus management — Focus Once / Focus Delay / Disable Focus / Prevent Kodi Focus per-app. Passive BPM detection means we don't fight focus.
- External players (4 slots) and external apps (3 groups × 3 slots) — both are Kodi-internal launcher concepts we don't reproduce.
- Shell replacement, MetroUI start, Confluence skin shutdown button.
- Portable Mode flag, iMON / XBMConiMON paths (legacy IR remote tooling).

**Config knobs:** `kodiPath`, `steamPath`, `launchOnStartup`, `bootDelaySec`, `wakeDelaySec`.

### 4.2 Remote power + status server (replaces RemoteShutdown)

`HttpListener` on **port 5009** (matches existing HA wiring; configurable). State-changing routes are token-guarded and CIDR-gated; status routes are public.

See §5 for the full route table.

**Delayed sleep UX (the core fix vs RemoteShutdown's modal dialog):**
- `POST /sleep?delay=60` returns `202` immediately and shows a **tray balloon** with countdown + "click to cancel".
- Cancellation: click the balloon, or `POST /sleep/cancel`.
- No modal dialog (RemoteShutdown's blocks the UI thread).

**Dropped from RemoteShutdown for v1:** shutdown, restart, forceshutdown, hibernate, turnscreenoff, lock, parental control (hide tray + password). Extension points left clean if any return.

### 4.3 Volume OSD — DEFERRED to v1.1

Design preserved for resumption:

- **Model:** HA pushes AVR volume → `POST /volume` → transparent click-through always-on-top WPF overlay.
- **Payload:** `{level, label, muted}`. RX-V685 math (`disp = round(volume_level*161)*0.5`, 0–80.5) computed in HA, not Valet.
- **Look:** Win11-style minimal flyout (position/scale/theme configurable).
- **Click-through:** `WS_EX_TRANSPARENT | WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`.
- **HA migration:** existing `Automations/Kodi-AVR-Volume-OSD.yaml` keeps trigger + `source == AV1` gate; swap action `kodi.call_method` → `rest_command` POST to `/volume`.

### 4.4 Toast notifications (new)

**Model:** HA POSTs `{title, body, icon?}` → Valet shows a Windows Toast via Action Center. Works over any app — toasts aren't owned by a specific window.

- **Endpoint:** `POST /notify` (token-required). Body: `{title, body, icon?, scenario?}`.
- **Library:** `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 — works cleanly with our WinForms host on `net10.0-windows10.0.19041.0`. (Tried `CommunityToolkit.WinUI.Notifications` first per the spec's rename note, but that one targets WinUI 3 / WindowsAppSDK which we don't need.)
- **Identity:** Windows toasts need an **AppUserModelID**. The installer registers `io.github.yogiee.Valet` (and `Program.cs` calls `SetCurrentProcessExplicitAppUserModelID` early on every start as a belt-and-braces).
- **Use cases:** HA pushing "Door opened", "Print finished", "AVR muted", etc. — anything HA already routes to other notify targets.

---

## 5. HTTP API

- **Base:** `http://igomedia.local:5009/` (LAN only).
- **Auth:** shared token (`authToken`). Either header `X-Auth-Token: <token>` or path segment `/<token>/<action>`. Status routes are token-free.
- **Binding:** CIDR allow-list (default `192.168.69.0/24`); off-LAN → 403.
- **Responses:** JSON, `Content-Type: application/json`.

| Method | Path | Auth | Purpose | Response |
|---|---|---|---|---|
| GET | `/` | none | Health/online check (safe default) | `200 {"app":"Valet","online":true}` |
| GET | `/status` | none | Rich state for HA | see below |
| GET | `/version` | none | Build info | `200 {"version":"x.y.z","commit":"..."}` |
| POST/GET | `/sleep` (alias `/suspend`, or `/<token>/sleep`) | token | Suspend, with optional `?delay=N` | `202 {"action":"sleep","delay":n}` |
| POST | `/sleep/cancel` | token | Cancel pending delayed sleep | `200 {"cancelled":true}` |
| POST | `/notify` | token | Show Windows Toast | `200 {"shown":true}` |

**`GET /status` body:**
```json
{
  "app": "Valet",
  "online": true,
  "state": "media",                                // media | gaming | booting
  "kodiRunning": true,
  "activity": "playing_video",                     // idle | playing_video | playing_audio | gaming | unknown
  "activityDetail": { "title": "Inception", "type": "movie" },
  "foreground": "kodi",                            // kodi | steam | desktop | other
  "uptimeSec": 12873
}
```

**`activity` derivation:**
- `gaming` if Steam BPM window is present.
- Otherwise query Kodi JSON-RPC at `http://localhost:8080/jsonrpc` → `Player.GetActivePlayers` + `Player.GetItem`:
  - Audio player → `playing_audio`
  - Video player → `playing_video` (with title/type in `activityDetail`)
  - No player → `idle`
- If Kodi JSON-RPC is unreachable (Kodi off, or "Allow remote control via HTTP" disabled) → `unknown`.

**`POST /notify` body:**
```json
{
  "title": "Door opened",
  "body": "Front door at 19:14",
  "icon": "info",                                  // info | warning | error | none
  "scenario": "default",                           // default | reminder | alarm
  "image": "http://ha.local/snapshots/door.jpg",   // optional image (http(s) or file://)
  "imagePlacement": "inline"                       // inline (default) | hero | logo
}
```

**Image placement** (per the [Microsoft toast schema](https://learn.microsoft.com/en-us/windows/apps/develop/notifications/app-notifications/app-notifications-content)):

| Placement | Banner pop-up | Notifications Center | Best for |
|---|---|---|---|
| `inline` (default) | not shown in banner | full image | Detail content seen on demand |
| `hero` | banner, **cropped to ~2:1** | banner | Wide snapshots, 16:9 sources |
| `logo` | small circular icon on the side | same | App icons, square avatars |

Notes:
- `http(s)` URLs are downloaded to `%TEMP%\Valet\toast-images\` before being passed to the toast (unpackaged Win32 toasts don't fetch URIs themselves). Stable filename per URL — repeated notifications reuse the path but content is always refreshed.
- `file://` URIs are passed through directly when the file exists.
- Other schemes (`data:`, `ftp:`, `ms-appx:`) are logged and the toast renders without an image.

**HA examples:**
```yaml
rest_command:
  htpc_sleep:
    url: "http://igomedia.local:5009/sleep"        # /suspend also works
    method: POST
    headers:
      X-Auth-Token: !secret valet_token

  htpc_notify:
    url: "http://igomedia.local:5009/notify"
    method: POST
    headers:
      X-Auth-Token: !secret valet_token
    content_type: "application/json"
    payload: '{"title":"{{ title }}","body":"{{ message }}","icon":"info"}'

binary_sensor:
  - platform: rest
    name: HTPC Online
    resource: "http://igomedia.local:5009/"
    value_template: "{{ value_json.online }}"
    scan_interval: 30
    timeout: 3

sensor:
  - platform: rest
    name: HTPC Activity
    resource: "http://igomedia.local:5009/status"
    value_template: "{{ value_json.activity }}"
    json_attributes_path: "$"
    json_attributes: ["state", "kodiRunning", "foreground", "activityDetail"]
    scan_interval: 15
```

---

## 6. Configuration

`%APPDATA%\Valet\config.json`, created with defaults on first run; editable via tray → Settings or by hand.

```jsonc
{
  // Paths (Browse pickers in Settings)
  "kodiPath": "auto",                        // or explicit path
  "steamPath": "auto",                       // or explicit path

  // Lifecycle
  "launchOnStartup": true,                   // toggling (un)registers the Task Scheduler logon task
  "bootDelaySec": 4,                         // wait before first reconciliation after app start
  "wakeDelaySec": 4,                         // wait before reconciliation after PowerModes.Resume

  // Server
  "httpPort": 5009,
  "authToken": "GENERATED_ON_FIRST_RUN",     // random 32-byte hex
  "allowedCidr": "192.168.69.0/24"
}
```

OSD knobs and any future advanced controls join this file when their modules ship.

---

## 7. Tray UI

`NotifyIcon` with context menu:

- **Status** (disabled label: e.g. `Media (Kodi playing)`, `Gaming (Steam BPM)`, `Idle`)
- **Launch Kodi** (force-reconcile — useful if Kodi was closed manually)
- **Sleep now**
- ─────
- **Settings…** → form with Kodi path picker, Steam path picker, launch-at-login toggle, boot delay, wake delay, port, token (with regenerate button), allowed CIDR. Nothing else exposed in UI.
- **Open log folder** (`%APPDATA%\Valet\logs`)
- **Exit** (optional PIN/confirm so it isn't closed by accident from the couch)

Tray tooltip mirrors Status. Tray icon may use a distinct glyph in `gaming` state.

> **UI polish (icon set, Settings layout, balloon styling) refined directly on the Windows machine by Yogi — spec fixes behavior, not pixels.**

---

## 8. Project structure

Already scaffolded; `dotnet build` succeeds. Current files match this layout (most are empty namespace stubs awaiting implementation):

```
Valet/
  Valet.slnx                    # .NET 10 XML solution
  DESIGN.md                     # this file
  CLAUDE.md                     # session orientation for future Claude runs
  .gitignore
  src/Valet/
    Valet.csproj                # net10.0-windows, WinForms+WPF, single-file publish, PerMonitorV2 DPI
    app.manifest
    Program.cs                  # entry, single-instance mutex (working)
    App/
      TrayApplication.cs        # NotifyIcon + menu + message loop (skeleton working)
      Config.cs                 # JSON load/save (stub)
    Kodi/
      KodiController.cs         # launch / graceful close / kill
      LifecycleStateMachine.cs  # reconciliation loop
      SteamWatcher.cs           # EnumWindows poll for BPM (new vs original spec)
      PowerEvents.cs            # SystemEvents.PowerModeChanged
    Power/
      PowerActions.cs           # Application.SetSuspendState + cancellable delayed sleep
    Server/
      HttpServer.cs             # HttpListener routing loop
      Auth.cs                   # token + CIDR
      Endpoints.cs              # /, /status, /sleep, /sleep/cancel, /notify, /version
    Notify/
      Toast.cs                  # CommunityToolkit.WinUI.Notifications wrapper (new)
    Osd/
      VolumeOverlayWindow.xaml(.cs)  # deferred to v1.1
      OsdController.cs                # deferred to v1.1
    Native/
      NativeMethods.cs          # P/Invoke (EnumWindows, GetWindowText, WM_CLOSE, AUMID...)
    Logging/
      Log.cs                    # rolling file log in %APPDATA%\Valet\logs
  installer/
    Valet.iss                   # Inno Setup (autostart task, firewall, AUMID for toasts)
  WORKSPACE/                    # gitignored — KodiLauncher + remote-shutdown-pc clones for triage
```

Changes vs original spec §8: dropped `Kodi/Watchdog.cs` and `Kodi/ForegroundManager.cs`; added `Kodi/SteamWatcher.cs` and `Notify/Toast.cs`.

---

## 9. Build, autostart, packaging

```powershell
dotnet publish src/Valet/Valet.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

**Autostart — Task Scheduler logon task (preferred over `HKCU\Run`):**
- Runs at user logon, interactive session (needed for tray + toasts + Kodi launch).
- "Run with highest privileges" so `/sleep` has the rights it needs.
- Installer registers it:
  ```powershell
  schtasks /Create /TN "Valet" /TR "\"C:\Program Files\Valet\Valet.exe\"" /SC ONLOGON /RL HIGHEST /F
  ```

**Firewall:**
```powershell
New-NetFirewallRule -DisplayName "Valet HTTP 5009" -Direction Inbound `
  -Protocol TCP -LocalPort 5009 -Action Allow -Profile Private -RemoteAddress 192.168.69.0/24
```

**AUMID for toast attribution:** installer registers `io.github.yogiee.Valet` so toasts show under "Valet", not under generic "Notifications".

**Installer:** Inno Setup (`installer/Valet.iss`). Steps: copy exe → create config dir + default config (random token) → register logon task → add firewall rule → register AUMID → optional Start-Menu shortcut.

---

## 10. Security

- Token required on `/sleep`, `/sleep/cancel`, `/notify`. Status routes are read-only.
- CIDR allow-list (default `192.168.69.0/24`) rejects off-LAN callers with 403.
- Random 32-byte hex token generated on first run; never logged.
- `?delay=N` + balloon-countdown prevents accidental immediate suspend from a stray call.
- Do not WAN-expose port 5009. Trusted home LAN only.

---

## 11. Open TODOs (settle during the build, on `igomedia`)

1. **Steam BPM window title** — verify the title substring on the current Steam build (`"Steam Big Picture Mode"` is expected). Fallback if it ever changes: enumerate `steam.exe`-owned windows whose dimensions ≈ primary monitor.
2. **Kodi JSON-RPC reachability** — confirm Kodi → Settings → Services → Control → "Allow remote control via HTTP" is enabled and port is `8080`. If unset, `/status activity` reports `unknown` for Kodi-side info; acceptable for v1.
3. **Kodi install path** on `igomedia` — confirm `%LOCALAPPDATA%\Programs\Kodi\kodi.exe` vs `C:\Program Files\Kodi\kodi.exe` for the default value.
4. ✅ **Toast library** — resolved: `Microsoft.Toolkit.Uwp.Notifications` 7.1.3 (NOT the CommunityToolkit WinUI 3 variant). TFM bumped to `net10.0-windows10.0.19041.0`.
5. **OSD design (v1.1)** — Win11-style minimal flyout matching the retired `script.securitycam mode:volume` overlay; deferred until v1 is shipping.
6. **Token rotation UX** — v1 is a hard cutover when the user clicks "regenerate" in Settings. Decide if a grace window is wanted later.

---

## 12. Build status

- ✅ Scaffold builds clean (`dotnet build` → 0 warnings, 0 errors).
- ✅ `Program.cs` + `App/TrayApplication.cs`: tray icon, "Sleep now", "Open log folder", "Exit". Single-instance mutex works.
- ✅ `App/Config.cs`: `%APPDATA%\Valet\config.json` with random 32-byte hex token generated on first run.
- ✅ `Logging/Log.cs`: rolling-friendly text log at `%APPDATA%\Valet\logs\valet.log`.
- ✅ `Power/PowerActions.cs`: immediate suspend + cancellable delayed suspend (`BeginDelayedSuspend` / `CancelPendingSuspend` / `PendingSecondsRemaining`).
- ✅ `Server/Auth.cs`: token header OR `/<token>/path` prefix; CIDR allow-list (loopback always allowed).
- ✅ `Server/HttpServer.cs`: tries `http://+:port/` first, falls back to `http://localhost:port/` if no URL ACL (with warning + the exact `netsh` command to fix it).
- ✅ `Server/Endpoints.cs`: `GET /`, `GET /status`, `GET /version`, `POST|GET /sleep` (alias `/suspend`), `POST /sleep/cancel`, `POST /notify`. Smoke-tested end-to-end on the dev box (all routes, both token styles, delay+cancel round trip, real toasts firing). Actual `Suspend` not exercised here — must validate on `igomedia`.
- ✅ `Notify/Toast.cs`: HKCU AUMID registration + `SetCurrentProcessExplicitAppUserModelID` so toasts attribute to "Valet". `ToastContentBuilder` fluent API; `scenario` mapped to `ToastScenario.Reminder`/`Alarm` when requested. Smoke-tested — toasts visible in Action Center.
- ✅ `Native/NativeMethods.cs`: P/Invokes for `EnumWindows`, `GetWindowText`, `GetWindowTextLength`, `IsWindowVisible`, `GetWindowThreadProcessId`, `FindWindow`, `PostMessage` (`WM_CLOSE=0x0010`), `GetForegroundWindow`.
- ✅ `Kodi/SteamWatcher.cs`: 500 ms `EnumWindows` poll for visible `steam.exe`-owned windows whose title contains "Big Picture". Logs transitions only, exposes `IsBigPictureActive`.
- ✅ `Kodi/KodiController.cs`: `IsRunning` / `Launch` (auto-detects `kodi.exe` under `%LOCALAPPDATA%\Programs\Kodi`, `%ProgramFiles%\Kodi`, `%ProgramFilesX86%\Kodi`) / `CloseGracefully(timeout)` via WM_CLOSE to window class `Kodi` (fallback `XBMC`) → wait → force-kill.
- ✅ `Kodi/PowerEvents.cs`: wraps `SystemEvents.PowerModeChanged` into `Suspending` / `Resuming` events.
- ✅ `Kodi/LifecycleStateMachine.cs`: 500 ms reconciliation loop with `Booting`/`Media`/`Gaming` states; boot/wake delay gates; on suspend → synchronous Kodi close (2 s); on resume → reset to Booting with `wakeDelaySec` gate; transitions only fire side effects on edge, not every tick. Smoke-tested on dev box — boot delay → media transition observed, no spurious BPM detections, Kodi-not-installed handled cleanly.
- ✅ `Server/Endpoints.cs` `GET /status` now reports `state`/`kodiRunning`/`foreground` from `LifecycleStateMachine` + `GetForegroundWindow` lookup (returns `kodi` | `steam` | `desktop` | `other`).
- ✅ `Kodi/KodiJsonRpc.cs`: HttpClient (500 ms timeout) wrapper for `http://localhost:8080/jsonrpc`. `ProbeActivityAsync()` calls `Player.GetActivePlayers` + `Player.GetItem`, returns `(activity, detail)` — `playing_video` / `playing_audio` / `idle` / `unknown`. `/status` short-circuits to `idle` when `kodiRunning=false` so unreachable-probe latency isn't on the happy path. Smoke-tested both paths (no-Kodi-process: 5 ms idle; spoof-process with no real JSON-RPC: 513 ms unknown).
- ✅ `App/SettingsForm.cs` + `App/AutostartTask.cs`: code-only WinForms dialog (AutoSize, `TableLayoutPanel` grid) with Browse pickers for Kodi/Steam, "Launch at Windows logon" toggle wired to `schtasks` install/uninstall, numeric spinners for delays/port, CIDR validation on save, token regenerator with confirm prompt. Restart-required changes (port/token/CIDR/delays) surface a non-blocking "restart Valet to take effect" message instead of fighting the single-instance mutex with `Application.Restart`. Smoke-tested visually by Yogi.
- ✅ `installer/Valet.iss` + `build.ps1`: Inno Setup script + one-shot publish-and-package script. Release self-contained publish verified (78 MB single-file `Valet.exe` at `src\Valet\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\`). Installer script (Files, schtasks logon task, firewall rule, URL ACL, Start Menu shortcut, postinstall launch, uninstall reversal incl. taskkill / schtasks delete / firewall remove / urlacl release) is written but **not yet compiled** — Inno Setup 6 is not installed on the dev box. `build.ps1` warns and exits cleanly with the winget install command when iscc is missing. `CloseApplications=yes` + `CloseApplicationsFilter=*.exe` already in for the auto-update silent-install path.
- ✅ Icons + branding: `Valet.ico` (coin) as `ApplicationIcon` (exe + taskbar + Alt+Tab), `Valet-tray.ico` embedded for `NotifyIcon`, `valet-coin-128.png` embedded for the About-tab logo. AUMID: `io.github.yogiee.Valet`. Settings reorganized into four tabs (Launcher / Server / Auto Update / About) with About showing the coin, version, blurb, GitHub link, log/config folder links, and `© Yogi Gharat (yogiee)`.
- ✅ Public repo created at <https://github.com/yogiee/valet> — initial commit pushed, MIT license, README with coin-icon header, KodiLauncher + remote-shutdown-pc credited.
- ✅ `Update/UpdateChecker.cs`: GitHub Releases poller. `CheckAsync()` hits `api.github.com/repos/yogiee/valet/releases/latest` (stable) or `/releases?per_page=10` (beta), parses the tag (`v0.1.0` → `Version`), compares to `Assembly.GetName().Version`, picks the `Valet-Setup-*.exe` asset, extracts an optional SHA256 from the release body. `DownloadAndInstallAsync()` downloads to `%TEMP%`, verifies SHA256 if present (else logs that TLS is the only check), and launches the installer with `/VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS`. Inno's Restart Manager flow closes Valet, installs, and restarts it. Startup auto-check fires 30 s after boot — on update available, **only shows a toast notification**; the user manually triggers install from Settings → Auto Update → Check for updates now (so the user is present to approve the UAC elevation prompt). Smoke-tested v0.1.0 → v0.1.1 upgrade: full chain validated through "Installer launched" at 9 s post-detect; install completion gated by UAC and confirmed by manual Yes.
- 🟡 OSD module still empty namespace stubs.

**Implementation order:**
1. ✅ `Server/HttpServer.cs` + `Server/Endpoints.cs` + `Server/Auth.cs` + `Power/PowerActions.cs` — `/`, `/status`, `/version`, `/sleep`, `/sleep/cancel` working.
2. ✅ `Notify/Toast.cs` + `POST /notify` — toasts fire end-to-end. Required TFM bump to `net10.0-windows10.0.19041.0` for UWP API access.
3. ✅ `Kodi/KodiController.cs` + `Kodi/PowerEvents.cs` + `Kodi/SteamWatcher.cs` + `Kodi/LifecycleStateMachine.cs` — reconciliation loop running. Boot/wake delay gates work; transition logging in place. Real Kodi launch, real BPM detection (window-title verification), real sleep/wake cycle need `igomedia` to validate.
4. ✅ `Kodi/KodiJsonRpc.cs` + `/status activity` enrichment — both code paths validated on dev box. Real `playing_video`/`playing_audio` detection needs Kodi running on `igomedia`.
5. ✅ `App/SettingsForm.cs` + `App/AutostartTask.cs` + tray "Settings…" entry — all knobs editable. Autostart task install needs admin elevation (handled gracefully — config saves, schtasks warning surfaces if not elevated); installer will register the task at install time on `igomedia`.
6. 🟡 `installer/Valet.iss` + `build.ps1` — script written, Release publish verified. Compile-to-installer-EXE pending Inno Setup install (`winget install --id JRSoftware.InnoSetup`).
7. ✅ **Auto Update client** — `Update/UpdateChecker.cs` + startup hook + Settings "Check now" button. Smoke-tested no-releases path. Full update cycle (download + silent install) needs an actual GitHub Release with an installer asset to validate.
8. **v1.1:** OSD module + `POST /volume` + HA migration off `script.securitycam`.
3. `Kodi/KodiController.cs` + `Kodi/PowerEvents.cs` + `Kodi/SteamWatcher.cs` + `Kodi/LifecycleStateMachine.cs` — boot → reconcile → sleep/wake → BPM handoff. Decommission KodiLauncher.
4. `/status activity` — wire Kodi JSON-RPC client once lifecycle state populates `state` and `foreground`.
5. `App/Config.cs` + Settings form — once knobs are settled.
6. `installer/Valet.iss` — once the above are validated end-to-end on `igomedia`.
7. **v1.1:** OSD module + `POST /volume` + HA migration off `script.securitycam`.

---

## Naming

**`Valet`** (final). Unobtrusive background-helper persona — fits Yogi's product-app naming taste (Alice, Jarvis), deliberately not Kodi-specific. Sets: exe `Valet.exe`, config dir `%APPDATA%\Valet\`, scheduled task `Valet`, tray label/tooltip, firewall rule, AUMID `io.github.yogiee.Valet`. (`Usher` was runner-up; valet's lower profile won.)

---

### Appendix A — P/Invoke + BCL surface

- **Sleep:** `Application.SetSuspendState(PowerState.Suspend, force: true, disableWakeEvent: true)` — the WinForms wrapper around `SetSuspendState` from `powrprof.dll`. RemoteShutdown uses this; raw P/Invoke isn't needed.
- **Wake / sleep detection:** `Microsoft.Win32.SystemEvents.PowerModeChanged` — `PowerModes.Suspend` and `PowerModes.Resume`.
- **Steam BPM detection:** `EnumWindows` + `GetWindowText` + `GetWindowThreadProcessId` (all `user32`) + `Process.GetProcessById(...).ProcessName`.
- **Graceful Kodi close:** `FindWindow(className: "Kodi", null)` (fallback `"XBMC"`) → `PostMessage(hwnd, WM_CLOSE, 0, 0)`. After 5 s: `Process.Kill()`.
- **OSD overlay (deferred):** `GetWindowLong` / `SetWindowLong` with `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST`.
- **Single instance:** named `Mutex` (wired in `Program.cs`).
- **AUMID:** `SetCurrentProcessExplicitAppUserModelID` from `shell32.dll`, called early in `Program.cs` so toasts attribute to Valet.

### Appendix B — Reference sources

Both cloned to `WORKSPACE/` (gitignored) for triage:

- **`WORKSPACE/KodiLauncher/`** — `baijuxavior/KodiLauncher`.
  - Settings GUI surface: `XBMCLauncherGUI/.../frmXBMCLauncherGUI.Designer.vb` (full tab list — most tabs dropped, see §4.1).
  - `WM_POWERBROADCAST` handler (the suspend/resume flow we mirror): `Script Files/Launcher4Kodi.ahk` lines 414–461.
  - Graceful close pattern (WM_CLOSE to `ahk_class XBMC`): `Script Files/CloseKodi.ahk`.
- **`WORKSPACE/remote-shutdown-pc/`** — `karpach/remote-shutdown-pc`.
  - HTTP routing + token handling: `Karpach.RemoteShutdown.Controller/Helpers/HostHelper.cs`.
  - Power actions (confirms `Application.SetSuspendState` as the right call): `Helpers/TrayCommandHelper.cs`.
  - Settings form layout (UX reference for our Settings window): `SettingsForm.cs` + `SettingsForm.Designer.cs`.
