# Insomnia for Windows

Keep your Windows laptop awake — even with the lid closed — for a session with an end time.

> **Unofficial Windows port of [Insomnia](https://github.com/krishhgg/Insomnia) by Krish Garg** (MIT License).
> All credit for the idea, design and recovery model goes to the original macOS app. This
> port was written from scratch in C# and reuses the original's design, behaviour, recovery
> model and wording. It is not affiliated with or endorsed by the original author.

> **Use a stable, well-ventilated surface, not a closed bag.** Insomnia is experimental.
> Recovery can fail, and a running timer is not a safety guarantee.

A C# / .NET 10 tray app: WPF (Windows 11 Fluent theme) for the UI, WinForms `NotifyIcon`
for the tray icon. No administrator rights needed. The UI is currently in Brazilian
Portuguese; UI strings are quoted as they appear in the app.

## What it does

- **Timed session**: presets (30 min to 3 days) or a free duration (`1h30m`, `3d`, `45`).
  The countdown shows in a pill at the top of the screen (`3h 42m`, `4m 59s`) and in the
  tray tooltip. Add time with `+15 min`, `+1 h`, `+4 h`; end early by **holding** the end
  button (0.6 s). A reminder appears 5 minutes before the end.
- **Lid closed without sleeping**: during a session, the lid action (`LIDACTION`) of the
  active power plan is set to "do nothing" on AC and on battery, and the app holds a power
  request (`PowerSetRequest` SystemRequired + ExecutionRequired) that dies with the process.
- **When the lid closes** (each action can be turned off in Settings > Tampa): turns the
  monitor off, mutes the default audio output, freezes the apps on your list (or every app
  with a window, except protected ones and the agent list), turns on Energy Saver, and
  optionally pauses Docker Desktop if it is idle. Opening the lid undoes all of it.
- **Battery floors**: turns on Energy Saver below N% and ends the session below M% (0
  disables the battery end).
- **Hotspot fallback**: if Wi-Fi drops during a session, it tries to connect to the saved
  Wi-Fi profile of your phone (5 s, then 5/10/20/30 s, without giving up). Drops are
  written to `Logs\handoffs.log`.
- **Browsers**: warns when Chrome, Edge or Brave run without
  `--disable-backgrounding-occluded-windows --disable-renderer-backgrounding` and offers to
  close and relaunch them with the flags, keeping the profile.
- **Recovery**: every change that outlives the app is journaled first, and a per-user
  scheduled task acts as a backstop (see [How recovery works](#how-recovery-works)).

## Install

### Download (recommended)

1. Install the [.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0).
2. Download `Insomnia.exe` from the [Releases](https://github.com/caioriccia/Insomnia-for-Windows/releases)
   page (framework-dependent single file, win-x64).
3. Put it somewhere permanent and run it. The installer script uses
   `%LOCALAPPDATA%\Programs\Insomnia\`, which is a good place if you install by hand too.

`Insomnia.exe` is **unsigned**, so Windows SmartScreen will warn. Click **More info**, then
**Run anyway**.

### From source

Requires the .NET 10 SDK.

```powershell
git clone https://github.com/caioriccia/Insomnia-for-Windows.git
cd Insomnia-for-Windows
.\installer\instalar.ps1            # publishes, copies, creates a shortcut and runs the diagnostic
```

### What it changes on your system

No administrator rights are needed. Insomnia changes:

- the lid action (`LIDACTION`) of the active power plan, only during a session;
- the Energy Saver battery threshold (`ESBATTTHRESHOLD`), set to 100 on battery only, while
  the lid is closed or a battery floor requires it;
- a per-user Task Scheduler task, `Insomnia Backstop`;
- `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Insomnia`, only if you enable
  start with Windows.

## Using it

Start a session from the tray icon: left-click opens the panel, where you pick a preset or
type a duration (`Outra duração (ex.: 45, 1h30m, 3d)`) and press **Iniciar**. During a
session the pill shows the time left. Click the pill, or drag it down (48 px or a quick
flick), to open the panel; there you can add time or hold `Segure para encerrar` to finish
early. Right-click the tray icon for the menu (session and machine state, warnings and
their actions, `Abrir painel` or `Iniciar sessão de …`, `Configurações…`, `Sair`). Quit is
only in the menu, to avoid quitting by accident.

You do not have to close the lid, and opening it does not end the session.

### Interface

Follows Windows 11 (Segoe UI Variable, Segoe Fluent Icons, light/dark and accent color,
switched live). Where Windows allows, the pill, panel and menu use Acrylic and Settings
uses Mica; if DWM refuses (transparency effects off, high contrast, Windows 10), the
window falls back to a solid theme background and is never black or invisible. With
"Animation effects" off in Accessibility, nothing animates.

- **Pill**: 32 px tall, centered at the top of the primary monitor, with the time left
  (`2d 4h`, `3h 42m`, `42m`, `4m 59s`, `42s`) and a state dot. The glyph shows the state:
  eye (normal), pause (lid closed), amber warning (recovery pending), amber clock (under
  5 min) and red (under 1 min). Hidden without a session. It never appears on the taskbar
  or in Alt+Tab and never takes focus.
- **Panel**: with a session: big countdown, `termina às HH:MM`, chips
  `+15 min`/`+1 h`/`+4 h`/`Outro…`, a state card (lid, battery, Wi-Fi, actions, last
  drop), a `Tampa fechada` strip when applicable, warnings with actions
  (`Relançar Chrome`, `Parar de esperar`) and `Segure para encerrar`. Without a session
  (opened from the tray): preset chips, the duration field and **Iniciar**. After starting,
  the panel shows the countdown for 1.5 s and collapses to the pill. It closes on click
  outside, `Esc`, a click on the bottom handle, or dragging it up.
- **Tray**: a monochrome glyph (closed eye idle, eye in session, eye with a pause badge
  with the lid closed, eye with an amber dot on warnings), white on a dark taskbar and
  black on a light one; the tooltip has the countdown. The menu opens on the monitor under
  the cursor.
- **Settings**: Windows 11 Settings style, with navigation on the left (Sessão, Tampa,
  Agentes, Bateria, Rede, App). Every change is saved immediately; closing only hides the
  window.
- **Keyboard**: `Esc` closes the panel and menu; `Enter` in the duration field starts
  (empty field = selected preset) or extends; `Tab` walks chips, field, buttons, links and
  the end button; **holding** `Space`/`Enter` on the end button counts as holding. In
  Settings, `Enter` in the add fields (preset, app, SSID) adds.
- **Fullscreen**: with a game, fullscreen video or presentation in the foreground, the
  pill hides and returns when it leaves. An open panel is not affected.
- **Multiple monitors**: pill, panel and Settings stay on the primary monitor (where the
  tray is); the menu opens on the cursor's monitor. Changing the primary monitor,
  unplugging one or changing scale repositions the pill. DPI is per monitor (PerMonitorV2).

## What happens when the lid closes

During a session, closing the lid does each of these (each can be turned off in
Settings > Tampa), and opening it undoes them. The timer keeps counting while the lid is
closed.

- **The monitor turns off** (`SC_MONITORPOWER`); it wakes on open.
- **The default audio output is muted**, and unmuted on open.
- **Apps on your freeze list are frozen** (or every app with a window, minus protected
  apps and the agent list, if you choose that), and resumed on open.
- **Energy Saver turns on** (`ESBATTTHRESHOLD=100`, battery only).
- **Docker Desktop is paused if idle.** Optional, off by default.

## How recovery works

Every change that outlives the app (`LIDACTION`, Energy Saver, frozen apps, mute) is
written to a **journal** (`%LOCALAPPDATA%\Insomnia\state.json`) *before* it is made. A
per-user Task Scheduler task, **`Insomnia Backstop`**, runs `Insomnia.exe --backstop` every
minute (on battery too). If there is no valid session and the journal is dirty, it undoes
everything. So killing the app, a crash or a reboot in the middle of a session does not
leave the lid action disarmed past the session's end. An unreadable journal is never
overwritten: it is kept as evidence and blocks new changes until someone looks at it.

This is a backstop, not a guarantee. If something goes wrong,
`Insomnia.exe --backstop --force` undoes whatever is in the journal; if the journal is
corrupted, restore by hand with
`powercfg /setacvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1`,
`powercfg /setdcvalueindex SCHEME_CURRENT SUB_BUTTONS LIDACTION 1` and
`powercfg /setactive SCHEME_CURRENT` (use your original values).

### Files

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Insomnia\Insomnia.exe` |
| Config | `%APPDATA%\Insomnia\config.json` |
| Journal, session, lock | `%LOCALAPPDATA%\Insomnia\` (`state.json`, `session.json`, `.recovery.lock`) |
| Log | `%LOCALAPPDATA%\Insomnia\Logs\insomnia.log` (+ `.1`, 1 MiB) |
| Task | `Insomnia Backstop`, root of Task Scheduler |
| Start with Windows | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Insomnia` |

`INSOMNIA_HOME=<folder>` relocates config, journal, session and logs into `<folder>`
(tests and dry runs).

## Differences from the macOS version

| Mac | Windows | What changes |
|---|---|---|
| `pmset disablesleep 1` (sudo) | `LIDACTION=0` on AC and DC in the active plan + the process's `PowerSetRequest` | No sudo. Only `LIDACTION` persists and is journaled; the power request goes away when the app dies. Shows in `powercfg /requests`. |
| sudoers | nothing | Preflight at start and in `--diagnostico`: if you cannot change the power plan (group policy), the session does not start. |
| Saved screen/keyboard brightness | Turns the monitor off (`SC_MONITORPOWER`) on close; wakes on open | Nothing is saved; external monitors also turn off; the keyboard backlight is left out. |
| `pmset -b lowpowermode` | `ESBATTTHRESHOLD=100`, battery only | Same. No effect on AC. |
| SIGSTOP/SIGCONT | `NtSuspendProcess`/`NtResumeProcess`, identity by creation time + BootId | Apps are identified by exe name (`slack.exe`). Store apps may be terminated by Windows while suspended. |
| Docker (`com.docker.docker`) | `Docker Desktop.exe` + `com.docker.backend.exe` | The WSL2 VM (`vmmemWSL`) belongs to the system and does **not** freeze; only the UI and backend pause. Optional, off by default. |
| launchd + codesign | `Insomnia Backstop` task in the user's Task Scheduler | No code signing; "armed" = the registered task matches the expected one. |
| Keychain + hotspot password | Wi-Fi profile saved by Windows | **The app stores no password**: connect to the hotspot once through Windows; the app only asks Windows to connect via the profile. |
| Menu bar | Pill at the top of the screen + panel (click or drag) + tray icon with menu (right-click) | The countdown lives in the pill; the tray icon is just the state glyph. |
| Login Items | `HKCU\...\Run` | No system approval needed. |

Not ported: heat rules, tmux, App Nap, saved brightness, hotspot password in the app.

## Diagnostics

Run at any time (`| Out-Host` makes PowerShell wait and show the output):

```powershell
& "$env:LOCALAPPDATA\Programs\Insomnia\Insomnia.exe" --diagnostico | Out-Host
$LASTEXITCODE   # 0 = nothing blocks the app; 1 = a problem (listed under [Resumo])
```

It shows paths, config, session, journal (each entry), `LIDACTION` AC/DC and scheme, the
**permission preflight** (re-reads and rewrites the same `LIDACTION`, under the recovery
lock, to prove you can change the power plan; `--sem-pre-voo` skips it),
`ESBATTTHRESHOLD`, battery/AC, lid, the backstop task state (Armed/Missing/Different/
Unknown), hotspot profile, browsers and flags, and whether the tray app is running.

Other modes: `Insomnia.exe --backstop [--force]` (recovery; `--force` acts even with a
valid session) and `Insomnia.exe --remover-backstop`.

## Uninstall

```powershell
.\installer\desinstalar.ps1         # undoes everything; removes task, shortcut and program (keeps config and log)
.\installer\desinstalar.ps1 -Purge  # same, also deleting config, journal and logs
```

## Development

Requires the .NET 10 SDK.

```powershell
dotnet build Insomnia.sln --disable-build-servers
dotnet test Insomnia.sln --disable-build-servers
```

Build output goes to `%LOCALAPPDATA%\InsomniaBuild\artifacts` (see `Directory.Build.props`),
outside any synced folder, since sync clients lock files during builds. Tests never touch
the system: every effect sits behind an interface and the tests use fakes.

Layout:

- `src/Insomnia.Core` — core logic (net10.0)
- `src/Insomnia.Windows` — Windows integrations (net10.0-windows)
- `src/Insomnia.App` — WPF/WinForms app, assembly `Insomnia` (net10.0-windows)
- `tests/` — one test project per source project
- `installer/` — `instalar.ps1`, `desinstalar.ps1`

## Manual test checklist

Nothing below runs in automated tests; it is what only a real laptop proves. First, note
the original state:

```powershell
powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION
powercfg /q SCHEME_CURRENT SUB_ENERGYSAVER ESBATTTHRESHOLD
```

`LIDACTION` values: `0x0` nothing, `0x1` sleep, `0x2` hibernate, `0x3` shut down.

1. **Install**: `.\installer\instalar.ps1`. The diagnostic should exit 0, with preflight
   `ok` and the task `ausente (Missing)` (normal before the first session).
2. **Start a short session** (e.g. 10 min) from the panel (left-click the tray). Check:
   - the pill with the countdown at the top of the screen, the tray icon with the open eye
     and the tooltip `Insomnia · … · termina HH:MM`;
   - `powercfg /q SCHEME_CURRENT SUB_BUTTONS LIDACTION` shows `0x0` on AC and DC;
   - Task Scheduler: `Insomnia Backstop` exists, every 1 minute, "Start the task only if
     the computer is on AC power" **unchecked**;
   - `--diagnostico`: session `válida até`, journal `sujo` with the `LIDACTION` entry,
     backstop `armada (Armed)`.
3. **Close the lid** for 2-3 min (with an external monitor, or with audio playing, or with
   a download/ping running to see it did not stop). On opening:
   - the laptop did not sleep (the download/ping kept going; the pill countdown continued);
   - the screen went off on close and back on at open; the sound returned to its previous
     volume;
   - apps on the freeze list (e.g. Slack) respond again;
   - with the charger unplugged, Energy Saver turned on with the lid closed and off after
     opening.
4. **More time**: in the panel, `+15 min` pushes the end; `termina às` and the tooltip show
   the new time.
5. **End by holding** (`Segure para encerrar`, in the panel): releasing before 0.6 s does
   not end it; holding ends it, the panel collapses and the pill disappears. `LIDACTION`
   returns to the noted value; the diagnostic shows the journal `limpo`.
6. **Kill the app** in the middle of a short session (5 min): Task Manager > Insomnia >
   End task. Until the session's end, `LIDACTION` stays `0x0` (by design). **Wait 1-2 min
   after the end time** and check:
   - `powercfg /q … LIDACTION` is back to the noted value;
   - `insomnia.log` has the backstop restoring line;
   - `session.json` is gone and the journal is clean (`--diagnostico`).
7. **Kill with the lid closed** (frozen apps and mute): start a short session (5 min) with
   an app from the freeze list open (e.g. WhatsApp) and audio playing. In a PowerShell,
   schedule the kill **before** closing the lid (opening the lid undoes the actions, so the
   app has to die with it closed): `Start-Sleep 30; Stop-Process -Name Insomnia -Force`.
   Close the lid right after and keep it closed for ~1 min; the app dies with the frozen
   app suspended and the sound muted. Open the lid and do **not** reopen Insomnia.
   **Wait 1-2 min after the end time** and check: the frozen app responds again, audio is
   back and `LIDACTION` is back to the noted value, all by the backstop.
8. **Restart in the middle of a session**: after login, opening the app continues the
   session (if still valid) or undoes it (if expired). Without opening the app, the
   backstop undoes it up to 1-2 min after the end.
9. **Battery**: with the charger unplugged and the "end below" floor above the current
   percentage, the session ends by itself and restores everything.
10. **Hotspot**: set your phone's SSID in Settings > Rede (after connecting to it once
    through Windows). With a session active, turn off the router: within seconds the laptop
    joins the hotspot; `Logs\handoffs.log` records the drop.
11. **Browser**: with Chrome open normally, the menu and panel show the warning that Chrome
    is throttled; "Relançar Chrome sem limitação" closes and reopens it with the same
    windows/profile; the diagnostic now shows `sem limitação`.
12. **Quit from the tray** with a session active restores everything before closing.
13. **Interface** (what no automated test sees):
    - **Pill on start**: opening the app with a still-valid session (or starting one) makes
      the pill appear, with a slight slide, centered at the top of the primary monitor;
      without a session, only the tray icon (closed eye). The pill does not appear on the
      taskbar or in Alt+Tab, and showing it does not take focus from the window in use.
    - **Dragging opens**: dragging the pill down makes the panel grow out of it, following
      the pointer; releasing early snaps back, releasing past ~48 px (or on a quick flick)
      opens. `Esc`, click outside and dragging the handle up close it.
    - **Lid closed shows the strip**: close and reopen the lid during a session; while
      closed (with an external monitor), the pill shows the pause glyph and the panel shows
      the `Tampa fechada` strip with what was done
      (`2 apps congelados · áudio mudo · tela apagada`).
    - **Near the end changes color**: in a 6 min session, under 5 min the pill switches to
      a clock and amber text with seconds (`4m 59s`), `+15 min` becomes an accent button
      and the subline shows `faltam N min`; under 1 min, red.
    - **Warning appears**: with Chrome open without the flags, the pill swaps the eye for
      the amber warning, the tray icon gets the amber dot and the panel shows the warning
      with `Relançar Chrome`.
    - **Settings saves**: `Configurações…` in the menu (or the panel's gear) opens the
      window; change a preset and a Tampa switch, close and reopen the app: the changes are
      in `%APPDATA%\Insomnia\config.json` and persist.
    - **Light/dark live**: with pill, panel and Settings open, switch the mode in Windows
      Settings > Personalization > Colors: everything switches immediately without a
      restart; the tray icon follows the taskbar color. With "Transparency effects" off the
      windows get a solid background (never black or invisible).
    - **150% DPI on the second monitor**: with an external monitor at 150% and the laptop at
      100% (or the reverse), make the external one primary: the pill moves to it, sharp and
      centered; the tray menu opens on the cursor's monitor and Settings opens sharp.
      Unplugging the monitor repositions the pill on the remaining one.
    - **Fullscreen**: a fullscreen video or presentation hides the pill; on exit it returns.
14. **Uninstall**: `.\installer\desinstalar.ps1`. `LIDACTION` equals the noted value, the
    `Insomnia Backstop` task is removed, shortcut and program folder are removed.

## License and credits

[MIT](LICENSE). Copyright (c) 2026 Krish Garg (original macOS app,
[krishhgg/Insomnia](https://github.com/krishhgg/Insomnia)) and Caio Riccioppo (Windows
port). The Windows port was written with AI assistance.
