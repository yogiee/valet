# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Current state

**Greenfield — no source yet.** The repo contains a single design document and is awaiting first implementation. Read this before doing anything:

- `DESIGN.md` — **single source of truth** for the build. Architecture, module specs, HTTP API contract, config schema, project layout, build/publish/autostart/firewall commands, and open TODOs are all there. Do not restate it here; read it.

The scaffolded project layout (`Valet.slnx` + `src/Valet/<module-dirs>` + `installer/`) already matches §8 of the spec.

`WORKSPACE/` is a **gitignored scratch dir** for external assets — cloned reference repos (e.g., the legacy KodiLauncher / RemoteShutdown source we triage features from), throwaway research, anything not part of the build. Disposable; don't put anything load-bearing in it.

## What this app is (one-paragraph orientation)

`Valet.exe` is a Windows 11 system-tray app that runs on an HTPC named **`igomedia`** (`192.168.69.195`). One process owns three responsibilities that previously lived in three separate stale apps: (1) Kodi lifecycle (launch on logon/wake, watchdog, gentle refocus, game-handoff to Steam etc.), (2) a small LAN HTTP server on port **5009** that Home Assistant calls for sleep/status/launch, and (3) a transparent click-through WPF overlay that renders an AVR volume OSD on top of Kodi and borderless games when HA pushes volume changes. Tech: **C# / .NET 8, `net8.0-windows`, WinForms tray + WPF overlay, single self-contained `win-x64` exe.**

## Load-bearing constraints (don't break these without thinking)

- **Port 5009 is fixed by existing HA wiring.** `shell_command.suspend_igomedia` already calls `http://igomedia.local:5009/suspend`. Keep `/suspend` as an alias for `/sleep` so HA keeps working during cutover. The old RemoteShutdown must be uninstalled before Valet binds the port.
- **`GET /` and `GET /status` are read-only and token-free.** The whole point of replacing RemoteShutdown is that the root path must be a safe online-check, not an action. Never let the default path trigger state changes.
- **All state-changing endpoints require the token** (`X-Auth-Token` header *or* `/<token>/<action>` path) **and** are gated by a CIDR allow-list (default `192.168.69.0/24`).
- **OSD is push-only from HA.** Valet does not hook Windows or Kodi volume — HA computes the value (it owns the AVR `media_player.rx_v685` entity) and POSTs to `/volume`. The Yamaha RX-V685 scale math (`round(volume_level*161)*0.5`, 0–80.5) lives in HA, not here.
- **Single instance enforced via named mutex.** Autostart is a Task Scheduler logon task with highest privileges (not `HKCU\Run`) — interactive session is required for Kodi launch, foreground steal, and the overlay.
- **Drop list — do NOT port from KodiLauncher:** Windows shell replacement, sidebar/Aero/gadget toggles, Win7 power tricks, legacy gamepad shell nav, AutoHotkey. The user explicitly chose startup-app + watchdog mode instead.

## Architecture in 30 seconds

A Kodi lifecycle state machine (`MEDIA ↔ GAMING`, plus `BOOTING`) drives almost everything:
- **MEDIA**: watchdog keeps `kodi.exe` alive; gently refocuses Kodi *only* when focus fell to desktop/Explorer, never when the user launched something deliberately.
- **GAMING** (entered via tray, hotkey, or `POST /launch?app=<id>`): close Kodi to free GPU/RAM, watch the launched process tree, relaunch Kodi on exit.
- **Wake** (`SystemEvents.PowerModeChanged` → `Resume`): force MEDIA. This is the single most important behavior inherited from KodiLauncher.

The HTTP server (`System.Net.HttpListener`) and the WPF overlay both live in the same process and read the same in-memory state from the lifecycle machine.

## Build & publish

Single self-contained exe:

```powershell
dotnet publish src/Valet/Valet.csproj -c Release -r win-x64 `
  --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Autostart task and firewall rule (run once, post-install — the Inno Setup installer will eventually do this):

```powershell
schtasks /Create /TN "Valet" /TR "\"C:\Program Files\Valet\Valet.exe\"" /SC ONLOGON /RL HIGHEST /F
New-NetFirewallRule -DisplayName "Valet HTTP 5009" -Direction Inbound `
  -Protocol TCP -LocalPort 5009 -Action Allow -Profile Private -RemoteAddress 192.168.69.0/24
```

Config lives at `%APPDATA%\Valet\config.json` (created with sane defaults on first run); logs at `%APPDATA%\Valet\logs`.

## Verify-on-real-hardware list

These behaviors **cannot be confirmed from a dev box** — only on `igomedia`:
- Overlay drawing over borderless-fullscreen games (true exclusive-fullscreen is acceptable to miss).
- Wake-from-sleep → Kodi relaunch/refocus.
- `/sleep` actually suspending under the logon task's privilege level.
- HA reaching `:5009` through the firewall rule.

Build modules **behind feature flags** and validate in this order per the spec's handoff notes: server first (status + sleep, wired to HA) → Kodi lifecycle → OSD last.

## Decommission order (when cutting over)

Build & validate Valet → disable KodiLauncher autostart + uninstall RemoteShutdown (frees 5009) → revert the modded `script.securitycam` `mode: volume` Kodi addon once the OS-level OSD is confirmed working.
