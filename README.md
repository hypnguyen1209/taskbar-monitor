# taskbar-monitor

Lightweight CPU + RAM usage charts on the Windows taskbar, with an integrated **Claude Code** API usage indicator (5-hour session and 7-day weekly quota with time-to-reset).

Forked from the original [taskbar-monitor](https://github.com/leandrolugaresi/taskbar-monitor) (built on top of [CS DeskBand](https://github.com/dsafa/CSDeskBand)) and adapted for personal use on Windows 11.

![demo](https://i.imgur.com/brSOeBn.png)

## What this fork changes

- **UI trimmed to CPU + RAM only** — disk/network/GPU graphs removed, layout reshaped as horizontal percent bars to fit the modern taskbar.
- **Claude usage panel** — polls the Anthropic API every 60s using the OAuth token from `%UserProfile%\.claude\.credentials.json` and renders two bars:
  - **CURRENT** — 5h unified session utilization + time until reset.
  - **WEEKLY** — 7d unified utilization + time until reset.
  - Bar hides automatically when no token / API unreachable.
- **Windows 11 positioning fix** — the original layout was anchored to `TrayNotifyWnd`, which on Win11 only contains the clock and system icons. App tray icons (Telegram, Steam, antivirus, …) are XAML widgets that live outside `TrayNotifyWnd`, so the control used to overlap them. The control now enumerates all right-anchored children of `Shell_TrayWnd` to compute the real boundary and sits cleanly to the left of the entire tray cluster. A 4s reposition timer handles dynamic tray icons (apps starting/stopping).
- **Installer fixed for Win11** — Win11 host runs as a standalone tray app, not a COM DeskBand. Installer no longer tries to `regasm` the DLL on Win11; legacy unregister is best-effort and won't crash the installer if the old DLL is corrupt or not a .NET assembly.
- **Code-quality fixes** across the shared assembly:
  - `Monitor.Dispose()` correct teardown order + singleton reset.
  - `drawGraph` clamp guard fixed (was assigning to the wrong variable).
  - All `Options.CounterOptions["..."]` accesses use `TryGetValue` with safe fallbacks.
  - `CounterCPU` core-name regex made locale-tolerant.
  - `Options.SaveToDisk` is now atomic (write `.tmp` + `File.Replace`), with corrupt-config fallback in `ReadFromDisk`.
  - `PerformanceCounterReader` mutates `Values` in place and forces a query re-init on transient PDH errors instead of swallowing them.
  - `ClaudeUsageMonitor` HTTP client has a 30 s timeout; polling respects the `EnableClaudeUsage` flag (no more billing the user's quota in the background when the bar is hidden).
  - `TaskbarManager` locks `TaskbarList` for cross-thread safety and guards `Invoke` against disposed controls.
  - `Program.cs` (Win11): null-safe finally block, plus `AppDomain.UnhandledException` reporting.
- **NuGet alignment** — `Newtonsoft.Json` reference moved from `12.0.3` to the actually-present `13.0.1` so the solution restores and builds out of the box.

## Requirements

- Windows 10 (DeskBand) or Windows 11 (standalone tray app).
- [.NET Framework 4.7.2](https://dotnet.microsoft.com/download/dotnet-framework/net472) (preinstalled on Windows 10 1803 April 2018 and later).
- *(Optional)* A Claude Code installation that has logged in at least once, so `%UserProfile%\.claude\.credentials.json` exists. Without it the Claude bar simply stays hidden.

## Install

1. Build the solution (`Release|Any CPU`) or download a prebuilt `TaskbarMonitorInstaller.exe`.
2. Run the installer **as Administrator** — it installs into `C:\Program Files\TaskbarMonitor`, registers itself in startup, and on Windows 11 launches the tray app immediately.

If a previous broken install left files behind, clean them first:

```powershell
# PowerShell as Administrator
Stop-Process -Name TaskbarMonitorWindows11 -Force -ErrorAction SilentlyContinue
Remove-Item "C:\Program Files\TaskbarMonitor" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "C:\Program Files (x86)\TaskbarMonitor" -Recurse -Force -ErrorAction SilentlyContinue
```

## Running without installing

```
TaskbarMonitorWindows11\bin\Release\TaskbarMonitorWindows11.exe
```

Right-click the tray icon for Settings / Open Task Manager / Open Resource Monitor / Exit.

## Uninstall

Remove via **Settings → Apps → Installed apps** (entry `taskbar-monitor`), or run the installer with `/uninstall`.

## Configuration

Stored at `%LocalAppData%\Programs\taskbar-monitor\config.json`. Notable keys:

- `EnableClaudeUsage` — toggle the Claude bar (and the network polling).
- `PollTime` — system counter polling interval in seconds.
- `ThemeType` — `AUTOMATIC` / `DARK` / `LIGHT` / `CUSTOM`.

The configuration file is rewritten atomically and self-heals on corruption.

## Credits

- Original project: [leandrolugaresi/taskbar-monitor](https://github.com/leandrolugaresi/taskbar-monitor)
- DeskBand framework: [dsafa/CSDeskBand](https://github.com/dsafa/CSDeskBand)
