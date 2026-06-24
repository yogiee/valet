<p align="center">
  <img src="assets/valet-coin-256.png" width="160" alt="Valet" />
</p>

<h1 align="center">Valet</h1>

<p align="center">
  A focused Windows 11 tray companion for HTPCs — launches Kodi, hands off to Steam Big Picture, answers Home Assistant on the LAN, and surfaces HA toasts on the TV.
</p>

<p align="center">
  <a href="https://github.com/yogiee/valet/releases"><img alt="Release" src="https://img.shields.io/github/v/release/yogiee/valet?include_prereleases&sort=semver" /></a>
  <a href="LICENSE"><img alt="License" src="https://img.shields.io/badge/license-MIT-blue" /></a>
  <img alt="Windows 11" src="https://img.shields.io/badge/Windows-11-0078d4" />
  <img alt=".NET 10" src="https://img.shields.io/badge/.NET-10-512bd4" />
</p>

---

## What it does

Valet runs as a single tray icon and consolidates three responsibilities that previously needed three separate tools, plus one new capability:

- **Kodi launcher.** Launches Kodi at logon (with optional boot delay), kills it cleanly when the system suspends, relaunches it on resume (with optional wake delay). Detects **Steam Big Picture** appearing on screen and gracefully closes Kodi to free GPU/RAM; relaunches Kodi when Big Picture exits.
- **Remote power + status server.** A small HTTP server on port `5009` (LAN only, token-guarded) exposes `/sleep`, `/sleep/cancel`, `/status`, `/version`, `/notify`, and `/volume`. Root `/` is a token-free health check — *safe* by default, unlike its predecessor.
- **Toast pass-through.** `POST /notify` from HA renders as a Windows Toast in the Action Center — visible regardless of which app is currently foreground.
- **AVR Volume OSD** — transparent click-through overlay rendering AVR volume changes pushed by HA, working over Kodi and borderless-fullscreen games. Position / scale / timeout configurable.

## Install

1. Download the latest `Valet-Setup-x.y.z.exe` from [Releases](https://github.com/yogiee/valet/releases).
2. Run as administrator. The installer registers a logon task (highest privileges), opens an inbound firewall rule for TCP 5009 (Private profile, LAN only), and reserves the HTTP URL ACL.
3. Right-click the **Valet** tray icon → **Settings…** to point at your `kodi.exe` and `steam.exe`, and adjust the allowed CIDR for your LAN.

Auto-update polls GitHub Releases on startup. When a new version is available, Valet shows a Windows toast — open **Settings → Auto Update → Check for updates now** to install. (User-triggered so you're present to approve the Windows UAC prompt the installer raises.)

## Home Assistant wiring

Drop these into your HA config and reference the secret `valet_token` (copy it from **Settings → Server → Auth token**):

```yaml
rest_command:
  htpc_sleep:
    url: "http://igomedia.local:5009/sleep"      # /suspend also works as alias
    method: POST
    headers:
      X-Auth-Token: !secret valet_token

  htpc_notify:
    url: "http://igomedia.local:5009/notify"
    method: POST
    content_type: "application/json"
    headers:
      X-Auth-Token: !secret valet_token
    # 'image' (http/https/file://) is optional. 'imagePlacement' picks how it renders:
    #   hero   — banner in the pop-up, cropped to ~2:1 (best for 16:9 snapshots)
    #   logo   — small circular icon on the side (best for square app icons)
    #   inline — full image, only visible in the Notifications Center expanded view (default)
    payload: '{"title":"{{ title }}","body":"{{ message }}","icon":"info","image":"{{ image | default(none) }}","imagePlacement":"hero"}'

  # Volume OSD — driven by an HA automation on the AVR. RX-V685 example:
  htpc_volume_osd:
    url: "http://igomedia.local:5009/volume"
    method: POST
    headers:
      X-Auth-Token: !secret valet_token
    content_type: "application/json"
    payload: >-
      {% set v = state_attr('media_player.rx_v685','volume_level') | float(0) %}
      {% set disp = ((v * 161) | round(0) | int) * 0.5 %}
      {% set bar = [100, (disp / 75.5 * 100) | round(0) | int] | min %}
      {% set muted = state_attr('media_player.rx_v685','is_volume_muted') %}
      {% set label = 'Mute' if (muted or disp <= 0) else ('MAX' if disp >= 80.5 else '%.1f'|format(disp)) %}
      {"level": {{ bar }}, "label": "{{ label }}", "muted": {{ (muted or disp <= 0)|lower }} }

automation:
  # Fires the OSD whenever the AVR volume changes — but only when HTPC is the active source.
  - alias: "HTPC — AVR Volume OSD"
    trigger:
      - platform: state
        entity_id: media_player.rx_v685
        attribute: volume_level
    condition:
      - condition: state
        entity_id: media_player.rx_v685
        attribute: source
        state: "AV1"
    action:
      - service: rest_command.htpc_volume_osd

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

`/status` returns:

```json
{
  "app": "Valet", "online": true,
  "state": "media",                       // media | gaming | booting
  "kodiRunning": true,
  "activity": "playing_video",            // idle | playing_video | playing_audio | gaming | unknown
  "activityDetail": { "title": "...", "type": "movie" },
  "foreground": "kodi",                   // kodi | steam | desktop | other
  "uptimeSec": 12873,
  "sleepPendingSec": null
}
```

`activity` other than `gaming` is pulled from Kodi's JSON-RPC (`http://localhost:8080/jsonrpc`). Enable it in **Kodi → Settings → Services → Control → "Allow remote control via HTTP"**.

## Volume OSD tuning

The OSD knobs live in `%APPDATA%\Valet\config.json`:

| Key | Default | Notes |
|---|---|---|
| `osdEnabled` | `true` | Set `false` to disable the overlay entirely |
| `osdPosition` | `"bottom-right"` | One of `top-center`, `bottom-center`, `top-right`, `bottom-right` |
| `osdTimeoutMs` | `2000` | Idle time before fade-out begins |
| `osdScale` | `1.0` | Multiplier for window dimensions (0.5 – 3.0) |

The OSD strips a trailing `dB` / ` dB` suffix from the `label` field so HA can keep sending the raw RX-V685 readout while the overlay displays just the number.

## Build from source

Requirements: [.NET 10 SDK](https://dotnet.microsoft.com/download), [Inno Setup 6](https://jrsoftware.org/isinfo.php) (for packaging the installer).

```powershell
# from the repo root
.\build.ps1
```

Output:
- `src\Valet\bin\Release\net10.0-windows10.0.19041.0\win-x64\publish\Valet.exe` — self-contained single-file (~78 MB)
- `dist\Valet-Setup-<version>.exe` — installer, if Inno Setup is present

Architecture, module specs, HTTP API, and configuration schema live in [`DESIGN.md`](DESIGN.md).

## Credits

Valet's scope and behavior are informed by two older tools whose ideas it inherits — sincere thanks to both authors:

- **[KodiLauncher](https://github.com/baijuxavior/KodiLauncher)** by Baiju Xavior — the original Kodi launch / wake-relaunch / close-on-sleep / Steam handoff pattern. Valet drops the Win7/8 cruft (shell replacement, Confluence-skin hooks, iMON IR support, watchdog) and replaces the manual game-handoff trigger with passive Big Picture detection, but the lifecycle DNA is from here.
- **[remote-shutdown-pc](https://github.com/karpach/remote-shutdown-pc)** by Karpach — the HTTP-server-for-power-actions pattern. Valet fixes the headline complaint (root `/` triggers shutdown instead of being a health check), trims the action list to just sleep, and replaces the modal abort dialog with a tray-balloon countdown.

## License

[MIT](LICENSE) © 2026 Yogi Gharat
