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

## Architecture

The plugin is a 1:1 port of the original Python implementation's business logic, with a few inevitable changes for the C# environment:

| Concern | Python | C# |
|---|---|---|
| Clipboard monitor | 350ms polling in a separate process | Event-driven via `AddClipboardFormatListener` (`WM_CLIPBOARDUPDATE`) inside Flow Launcher's process |
| Reading clipboard | `ImageGrab` (PIL) + ctypes for `CF_HDROP` / `CF_UNICODETEXT` | Pure P/Invoke in [Win32Clipboard.cs](Win32Clipboard.cs) — CF_DIB → BMP reconstruction → PNG, CF_HDROP → `DragQueryFileW` |
| Read priority | HDROP > Text (only if no DIB) > Image | Same |
| Writing image | `PIL.Image.save("BMP")` → strip 14-byte header → `CF_DIB` | Same: `BmpBitmapEncoder` → strip 14 bytes → `SetClipboardData(CF_DIB, ...)` |
| Writing files | Hand-pack `DROPFILES` + UTF-16LE | Same: hand-built struct + `Encoding.Unicode` |
| Paste action | Spawn detached `delayed_paste.py` (sleep 10ms → `SendInput Ctrl+V`) | Spawn `PasteHelper.exe` (net48 console, sleep `paste_delay_ms` → `SendInput Ctrl+V`). The process boundary is essential — in-process `SendInput` races with Flow Launcher's window hide and focus transfer |
| Self-capture suppression | (not handled) | Listener ignores `WM_CLIPBOARDUPDATE` for 800ms after the plugin's own writes |

## Changelog

### 0.4.1

- History is now strictly newest-first. Two fixes:
  1. Each history `Result` sets `AddSelectedCount = false` — previously Flow Launcher's built-in frecency bumped frequently-pasted older items above newer captures.
  2. SQL `ORDER BY created_at DESC, id DESC` — second key breaks ties when two captures happen in the same second (autoincrement `id` always increases).
- Score widened to `int.MaxValue - index` so FL's internal score additions can never reorder the list.

### 0.4.0

- Full rewrite of the clipboard layer to match the Python implementation exactly. All clipboard reads and writes now use Win32 P/Invoke (`CF_UNICODETEXT`, `CF_DIB`, `CF_HDROP`) instead of WPF `Clipboard.*`.
- Paste now spawns a separate `PasteHelper.exe` (net48 console) to send Ctrl+V after a short delay. This matches Python's `delayed_paste.py` model — the process boundary is required so Flow Launcher can finish hiding and the target window can reclaim focus before Ctrl+V fires. Tunable via `paste_delay_ms` in `Data/settings.json` (default 80ms).
- Clipboard listener now suppresses captures for 800ms after the plugin writes to the clipboard, preventing the plugin's own paste / copy actions from being re-recorded as new history entries.
- Cleanup now runs **only** on plugin Init (FL startup) and after `c keep N` — never on every query.
- `c status` now reports whether `PasteHelper.exe` is present alongside the dll.

### 0.3.1

- Paste timing fixed. The previous `DispatcherPriority.Background` approach only sequenced work within WPF's dispatcher — it did not wait for Windows to transfer keyboard focus away from Flow Launcher, so `Ctrl+V` could still hit FL's own search box. Now polls `GetForegroundWindow()` (5ms intervals, up to 250ms) and fires `SendInput` the instant FL loses foreground.

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
