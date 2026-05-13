import ctypes
import os
import sys
import time
from pathlib import Path

PLUGIN_DIR = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parents[1]
for rel in (".", "lib", "plugin"):
    sys.path.insert(0, str(PLUGIN_DIR / rel))

from plugin.clipboard_bridge import read_clipboard_snapshot
from plugin.logger import log, log_exception
from plugin.settings import Settings
from plugin.storage import ClipboardStore

HEARTBEAT_INTERVAL = 2.0


def main():
    data_dir = PLUGIN_DIR / "Data"
    data_dir.mkdir(exist_ok=True)
    (data_dir / "monitor.pid").write_text(str(os.getpid()), encoding="utf-8")
    log(PLUGIN_DIR, f"monitor started pid={os.getpid()}")

    store = ClipboardStore(PLUGIN_DIR)
    settings = Settings(PLUGIN_DIR)
    last_sequence = _clipboard_sequence()
    last_heartbeat = 0.0

    while True:
        try:
            now = time.time()
            if now - last_heartbeat >= HEARTBEAT_INTERVAL:
                _write_heartbeat(last_sequence)
                last_heartbeat = now

            sequence = _clipboard_sequence()
            if sequence != last_sequence:
                last_sequence = sequence
                log(PLUGIN_DIR, f"clipboard sequence changed sequence={sequence}")
                _capture(store)
                store.cleanup(settings.keep_days)
        except Exception:
            log_exception(PLUGIN_DIR, "monitor loop failed")
        time.sleep(0.35)


def _capture(store):
    snapshot = read_clipboard_snapshot(PLUGIN_DIR)
    if not snapshot:
        log(PLUGIN_DIR, "clipboard changed but no supported snapshot was found")
        return

    source_app = _foreground_process_name()
    kind = snapshot["kind"]
    log(PLUGIN_DIR, f"captured clipboard kind={kind} source_app={source_app!r}")
    if kind == "text":
        store.add_text(snapshot["text"], source_app, snapshot["hash"])
    elif kind == "image":
        store.add_image(snapshot["image_path"], snapshot["preview_path"], source_app, snapshot["hash"])
    elif kind == "files":
        store.add_files(snapshot["paths"], source_app, snapshot["hash"])
    _write_heartbeat(_clipboard_sequence(), kind)


def _write_heartbeat(sequence, last_kind=""):
    path = PLUGIN_DIR / "Data" / "monitor.heartbeat"
    path.write_text(
        f"{time.time()}\n{os.getpid()}\n{sequence}\n{last_kind}\n",
        encoding="utf-8"
    )


def _clipboard_sequence():
    return ctypes.windll.user32.GetClipboardSequenceNumber()


def _foreground_process_name():
    try:
        hwnd = ctypes.windll.user32.GetForegroundWindow()
        pid = ctypes.c_ulong()
        ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        return _process_name(pid.value)
    except Exception:
        return ""


def _process_name(pid):
    PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
    handle = ctypes.windll.kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not handle:
        return ""
    try:
        size = ctypes.c_ulong(32768)
        buffer = ctypes.create_unicode_buffer(size.value)
        ok = ctypes.windll.kernel32.QueryFullProcessImageNameW(handle, 0, buffer, ctypes.byref(size))
        if not ok:
            return ""
        return os.path.basename(buffer.value)
    finally:
        ctypes.windll.kernel32.CloseHandle(handle)


if __name__ == "__main__":
    main()
