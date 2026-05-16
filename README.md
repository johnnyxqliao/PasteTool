# Paste Tool

Flow Launcher persistent clipboard history plugin (C# / .NET 7).

## Features

- `c` shows clipboard history, newest first.
- `c keyword` searches text content and copied file paths.
- Supports text, images, and copied file lists.
- Enter restores the selected record to the Windows clipboard and sends `Ctrl+V`.
- Context menu (`Shift+Enter` or right arrow) includes "copy only" without pasting.
- `c clear` clears all stored records and cached images.
- `c settings` shows retention options.
- `c keep 7`, `c keep 14`, `c keep 30` changes history retention.
- `c status` shows listener state and record count.

## How it works

The plugin loads as a .NET assembly into the Flow Launcher process. `Init` registers a Win32 clipboard listener (`AddClipboardFormatListener`) on a hidden message-only window. Every clipboard change fires `WM_CLIPBOARDUPDATE`, which captures and stores the snapshot — **no polling, no separate process, monitoring starts the instant Flow Launcher starts**, regardless of whether the user has typed the `c` keyword.

## Build / Release on GitHub

Push this repository to GitHub. The `Release` workflow (Actions tab) runs on `windows-latest`:

1. `actions/setup-dotnet` (7.0.x)
2. `dotnet publish -c Release -r win-x64 --no-self-contained` produces all dependencies in `publish/`.
3. Zips `publish/*` into `Flow.Launcher.Plugin.PasteTool.zip`.
4. Publishes a GitHub Release tagged with `plugin.json`'s `Version`.

## Local build

```pwsh
dotnet publish Flow.Launcher.Plugin.PasteTool.csproj -c Release -r win-x64 --no-self-contained -o publish
```

Copy `publish/*` into `%APPDATA%\FlowLauncher\Plugins\Flow.Launcher.Plugin.PasteTool-<version>\` and restart Flow Launcher.

## Install

Download `Flow.Launcher.Plugin.PasteTool.zip` from the GitHub Release, extract into Flow Launcher's plugin folder, then restart Flow Launcher.

## Migration from 0.1.x (Python)

0.2.0 is a full rewrite in C#. The old Python history database is not migrated — existing records are abandoned and a fresh `Data/history.sqlite3` is created on first run.

## Changelog

### 0.2.0

- Full rewrite in C# / .NET 7 (WPF).
- Clipboard monitoring is now event-driven via `AddClipboardFormatListener` and starts on Flow Launcher launch — no need to first type `c`.
- Removes the Python dependency and the separate monitor / delayed-paste helper processes.

### 0.1.x

- Python implementation. See git history for details.
