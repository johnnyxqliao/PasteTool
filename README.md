# Paste Tool

Flow Launcher persistent clipboard history plugin.

## Features

- `c` shows clipboard history, newest first.
- `c keyword` searches text content and copied file paths.
- Supports text, images, and copied file lists.
- Enter restores the selected record to the Windows clipboard and sends `Ctrl+V`.
- Context menu, opened with `Shift+Enter` or right arrow, includes "copy only" without pasting.
- `c clear` clears all stored records and cached images.
- `c settings` shows retention options.
- `c keep 7`, `c keep 14`, `c keep 30` changes history retention.

## Build On GitHub

Push this repository to GitHub, then run the `Release` workflow from the Actions tab.

The workflow builds `Flow.Launcher.Plugin.PasteTool.zip` on `windows-latest`, installs all Python dependencies into `lib`, uploads the zip as an artifact, and publishes a GitHub Release when run on `main`.

## Install

Download `Flow.Launcher.Plugin.PasteTool.zip` from the GitHub Release, extract it into Flow Launcher's plugin folder, then reload plugins or restart Flow Launcher.

## Notes

Flow Launcher's Python JSON-RPC results do not expose a reliable selected-row `Ctrl+C` hook. The plugin therefore maps:

- `Enter`: copy and paste.
- Context menu item "只复制到剪贴板": copy only.

If Flow Launcher adds a JSON-RPC shortcut hook later, this can be changed to make `Ctrl+C` directly copy the selected history item.

## Changelog

### 0.1.1

- Removed `pywin32` and switched clipboard operations to built-in Windows APIs through `ctypes`.
- Fixes `ImportError: DLL load failed while importing win32api` under Flow Launcher's embedded Python.

### 0.1.2

- Adds `【Text】`, `【Image】`, and `【File】` labels to result titles.
- Sends paste from a delayed helper process so the target app can regain focus before `Ctrl+V`.
