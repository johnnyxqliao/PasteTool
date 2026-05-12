import hashlib
import os
import struct
import time
from pathlib import Path

import win32api
import win32clipboard
import win32con
from PIL import Image, ImageGrab


def read_clipboard_snapshot(plugin_dir: Path):
    has_dib = False
    if not _open_clipboard():
        return None
    try:
        if win32clipboard.IsClipboardFormatAvailable(win32con.CF_HDROP):
            paths = list(win32clipboard.GetClipboardData(win32con.CF_HDROP))
            if paths:
                normalized = [str(Path(path)) for path in paths]
                return {
                    "kind": "files",
                    "paths": normalized,
                    "hash": _hash_text("\n".join(normalized))
                }

        has_dib = win32clipboard.IsClipboardFormatAvailable(win32con.CF_DIB)
        if win32clipboard.IsClipboardFormatAvailable(win32con.CF_UNICODETEXT):
            text = win32clipboard.GetClipboardData(win32con.CF_UNICODETEXT)
            if text and not has_dib:
                return {
                    "kind": "text",
                    "text": text,
                    "hash": _hash_text(text)
                }
    finally:
        try:
            win32clipboard.CloseClipboard()
        except Exception:
            pass

    image = ImageGrab.grabclipboard()
    if isinstance(image, Image.Image):
        image = image.convert("RGBA")
        content_hash = _hash_image(image)
        rel_image = Path("Data") / "images" / f"{content_hash}.png"
        rel_preview = Path("Data") / "images" / f"{content_hash}_thumb.png"
        image_path = plugin_dir / rel_image
        preview_path = plugin_dir / rel_preview
        image_path.parent.mkdir(parents=True, exist_ok=True)
        if not image_path.exists():
            image.save(image_path, "PNG")
        if not preview_path.exists():
            preview = image.copy()
            preview.thumbnail((128, 128))
            preview.save(preview_path, "PNG")
        return {
            "kind": "image",
            "image_path": str(rel_image),
            "preview_path": str(rel_preview),
            "hash": content_hash
        }

    return None


def set_record_to_clipboard(record):
    kind = record["kind"]
    if kind == "text":
        _set_text(record["content"] or "")
    elif kind == "image":
        _set_image(Path(record["image_path"]))
    elif kind == "files":
        _set_files(record.get("files") or [])


def paste_record(record):
    set_record_to_clipboard(record)
    time.sleep(0.08)
    _send_ctrl_v()


def _set_text(text):
    if not _open_clipboard():
        return
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardData(win32con.CF_UNICODETEXT, text)
    finally:
        win32clipboard.CloseClipboard()


def _set_image(path):
    if not path.is_absolute():
        path = Path(__file__).resolve().parents[1] / path
    image = Image.open(path).convert("RGB")
    dib = _image_to_dib(image)
    if not _open_clipboard():
        return
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardData(win32con.CF_DIB, dib)
    finally:
        win32clipboard.CloseClipboard()


def _set_files(paths):
    payload = _build_hdrop(paths)
    if not _open_clipboard():
        return
    try:
        win32clipboard.EmptyClipboard()
        win32clipboard.SetClipboardData(win32con.CF_HDROP, payload)
    finally:
        win32clipboard.CloseClipboard()


def _image_to_dib(image):
    import io

    output = io.BytesIO()
    image.save(output, "BMP")
    data = output.getvalue()
    return data[14:]


def _build_hdrop(paths):
    files = "\0".join(str(Path(path)) for path in paths) + "\0\0"
    encoded = files.encode("utf-16le")
    dropfiles = struct.pack("<IiiII", 20, 0, 0, 0, 1)
    return dropfiles + encoded


def _send_ctrl_v():
    win32api.keybd_event(win32con.VK_CONTROL, 0, 0, 0)
    win32api.keybd_event(ord("V"), 0, 0, 0)
    win32api.keybd_event(ord("V"), 0, win32con.KEYEVENTF_KEYUP, 0)
    win32api.keybd_event(win32con.VK_CONTROL, 0, win32con.KEYEVENTF_KEYUP, 0)


def _open_clipboard(retries=12, delay=0.04):
    for _ in range(retries):
        try:
            win32clipboard.OpenClipboard()
            return True
        except Exception:
            time.sleep(delay)
    return False


def _hash_text(text):
    return hashlib.sha256(text.encode("utf-8", errors="ignore")).hexdigest()


def _hash_image(image):
    import io

    output = io.BytesIO()
    image.save(output, "PNG")
    return hashlib.sha256(output.getvalue()).hexdigest()
