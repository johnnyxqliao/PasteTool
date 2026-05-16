# Paste Tool

Flow Launcher persistent clipboard history plugin (C# / .NET 7).

## Features

- `c` shows clipboard history, newest first.
- `c keyword` searches text content and copied file paths.
- Supports text, images, and copied file lists.
- **Enter**: instantly paste the selected record to the cursor position (writes clipboard, then sends `Ctrl+V` on the dispatcher's background priority so it fires the moment Flow Launcher's window has hidden — no fixed-time wait).
- **Ctrl+C**: copies the selected entry to the clipboard via Flow Launcher's built-in shortcut. Works directly for text. For images/files this only copies the path string — to put the actual image / file drop on the clipboard, use the context menu's "只复制到剪贴板".
- Context menu (`Shift+Enter` or right arrow) includes "copy only" (rich copy for images/files) and "copy + paste".
- `c clear` clears all stored records and cached images/files.
- `c settings` shows retention options.
- `c keep N` sets history retention to any positive integer of days (default `7`). Presets shown: 7 / 14 / 21.
- Files copied to the clipboard are **also cached** under `Data/files/<hash>/` so they can be re-pasted even if the original is moved or deleted. Single files above 100MB are not cached (configurable via `max_cached_file_size_mb` in `Data/settings.json`).
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

### 0.3.0

- File entries now cache the actual file bytes under `Data/files/<hash>/` (per-file size limit `max_cached_file_size_mb`, default 100MB). Re-paste still works after the original is moved or deleted; oversize files fall back to the original-path behavior.
- Default retention is now **7 days**. `c keep N` accepts any positive integer. Presets shown: 7 / 14 / 21.
- Selected results expose `CopyText`, so Flow Launcher's built-in `Ctrl+C` copies the entry's content (text) or path (image/files) to the clipboard.
- `Enter` paste is now scheduled at `DispatcherPriority.Background` instead of a fixed-time sleep — fires the instant Flow Launcher's window has hidden.

### 0.2.0

- Full rewrite in C# / .NET 7 (WPF).
- Clipboard monitoring is now event-driven via `AddClipboardFormatListener` and starts on Flow Launcher launch — no need to first type `c`.
- Removes the Python dependency and the separate monitor / delayed-paste helper processes.

### 0.1.x

- Python implementation. See git history for details.
