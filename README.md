# Lighter

**A lightweight Windows startup manager in a single .exe file.** No installer, no runtime to download — it uses the .NET Framework 4.8 that ships with Windows.

[Русская версия](README.ru.md)

<!-- Add a screenshot taken on a clean machine: docs/screenshot.png -->

## Features

- **Startup** — registry `Run` / `RunOnce` keys (HKCU, HKLM, 32-bit) and the Startup folders.
- **Task Scheduler** — tasks that run at logon or at boot (system tasks are hidden by default).
- **Services** — automatic-start services; disable and later restore the original start type.
- **Processes** — running processes with memory and publisher; marks the ones started by autostart; end a process.
- **Enable / disable** works exactly like Task Manager (`StartupApproved`), so Task Manager shows the same state.
- **Delete with a backup** — every deleted entry is saved first (`.reg`, `.lnk` or task `.xml`) to `%LOCALAPPDATA%\Lighter\Backup`; double-click the file to restore.
- Dark and light themes, Russian and English UI, smooth animations, search and sorting.

## Download

Get `Lighter.exe` from [Releases](../../releases). Each release lists the file's SHA-256 checksum.

The exe asks for administrator rights (UAC): HKLM keys, services and system tasks need them.
It is not code-signed, so Windows SmartScreen may show "Unknown publisher" on first run.

**Requirements:** Windows 10 / 11 or Windows Server 2016+ (.NET Framework 4.8 is built in).

## Keyboard

| Key | Action |
|---|---|
| Space | Enable / disable the selected entries |
| Del | Delete the entry / end the process |
| F5 | Refresh |
| Ctrl+F | Search |
| Ctrl+1…4 | Switch tabs |

## What Lighter changes on your system

| Action | Where |
|---|---|
| Enable / disable a registry or Startup-folder entry | `…\Explorer\StartupApproved\Run`, `Run32`, `StartupFolder` (same as Task Manager) |
| Enable / disable a scheduled task | the task's *Enabled* flag |
| Disable a service | start type → *Disabled*; the original type is kept in `HKLM\SOFTWARE\Lighter\DisabledServices` |
| Delete | backup in `%LOCALAPPDATA%\Lighter\Backup`, then the entry is removed |
| Settings (theme, language) | `HKCU\Software\Lighter` |

## Build from source

No SDK or Visual Studio needed — the C# compiler that ships with Windows is enough:

```
build.cmd
```

The result is `bin\Lighter.exe`. The exe icon is generated during the build by the same code that draws the in-app logo (`src\Logo.cs`, `tools\MakeIcon.cs`).

Source layout: `src\*Source.cs` read the autostart locations, `src\MainForm.cs` is the window,
`src\Controls.cs`, `src\ListViewEx.cs`, `src\Transition.cs` and `src\Anim.cs` are the custom-drawn,
animated UI, `src\Lang.cs` holds the language switch.

## License

[MIT](LICENSE) © DenHaIz
