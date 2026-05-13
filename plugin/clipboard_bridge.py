import ctypes
import hashlib
import struct
import subprocess
import sys
import time
from pathlib import Path

from PIL import Image, ImageGrab

from plugin.logger import log, log_exception

CF_UNICODETEXT = 13
CF_DIB = 8
CF_HDROP = 15
GMEM_MOVEABLE = 0x0002
KEYEVENTF_KEYUP = 0x0002
INPUT_KEYBOARD = 1
VK_CONTROL = 0x11
VK_V = 0x56

kernel32 = ctypes.windll.kernel32
shell32 = ctypes.windll.shell32
user32 = ctypes.windll.user32

kernel32.GlobalAlloc.argtypes = [ctypes.c_uint, ctypes.c_size_t]
kernel32.GlobalAlloc.restype = ctypes.c_void_p
kernel32.GlobalLock.argtypes = [ctypes.c_void_p]
kernel32.GlobalLock.restype = ctypes.c_void_p
kernel32.GlobalUnlock.argtypes = [ctypes.c_void_p]
kernel32.GlobalUnlock.restype = ctypes.c_bool
kernel32.GlobalFree.argtypes = [ctypes.c_void_p]
kernel32.GlobalFree.restype = ctypes.c_void_p
kernel32.GlobalSize.argtypes = [ctypes.c_void_p]
kernel32.GlobalSize.restype = ctypes.c_size_t

user32.OpenClipboard.argtypes = [ctypes.c_void_p]
user32.OpenClipboard.restype = ctypes.c_bool
user32.CloseClipboard.argtypes = []
user32.CloseClipboard.restype = ctypes.c_bool
user32.EmptyClipboard.argtypes = []
user32.EmptyClipboard.restype = ctypes.c_bool
user32.SetClipboardData.argtypes = [ctypes.c_uint, ctypes.c_void_p]
user32.SetClipboardData.restype = ctypes.c_void_p
user32.GetClipboardData.argtypes = [ctypes.c_uint]
user32.GetClipboardData.restype = ctypes.c_void_p
user32.IsClipboardFormatAvailable.argtypes = [ctypes.c_uint]
user32.IsClipboardFormatAvailable.restype = ctypes.c_bool
user32.EnumClipboardFormats.argtypes = [ctypes.c_uint]
user32.EnumClipboardFormats.restype = ctypes.c_uint

shell32.DragQueryFileW.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_wchar_p, ctypes.c_uint]
shell32.DragQueryFileW.restype = ctypes.c_uint


class KEYBDINPUT(ctypes.Structure):
    _fields_ = [
        ("wVk", ctypes.c_ushort),
        ("wScan", ctypes.c_ushort),
        ("dwFlags", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("dwExtraInfo", ctypes.c_void_p),
    ]


class INPUT_UNION(ctypes.Union):
    _fields_ = [("ki", KEYBDINPUT)]


class INPUT(ctypes.Structure):
    _fields_ = [
        ("type", ctypes.c_ulong),
        ("union", INPUT_UNION),
    ]


user32.SendInput.argtypes = [ctypes.c_uint, ctypes.POINTER(INPUT), ctypes.c_int]
user32.SendInput.restype = ctypes.c_uint


def read_clipboard_snapshot(plugin_dir: Path):
    has_dib = False
    if not _open_clipboard():
        log(plugin_dir, "read clipboard failed: could not open clipboard")
        return None
    try:
        formats = _available_formats()
        log(plugin_dir, f"read clipboard available_formats={formats}")
        if user32.IsClipboardFormatAvailable(CF_HDROP):
            paths = _read_files()
            if paths:
                normalized = [str(Path(path)) for path in paths]
                return {
                    "kind": "files",
                    "paths": normalized,
                    "hash": _hash_text("\n".join(normalized))
                }

        has_dib = user32.IsClipboardFormatAvailable(CF_DIB)
        if user32.IsClipboardFormatAvailable(CF_UNICODETEXT):
            text = _read_text()
            if text and not has_dib:
                return {
                    "kind": "text",
                    "text": text,
                    "hash": _hash_text(text)
                }
    finally:
        user32.CloseClipboard()

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


def _available_formats():
    formats = []
    current = 0
    while True:
        current = user32.EnumClipboardFormats(current)
        if current == 0:
            break
        formats.append(current)
    return formats


def set_record_to_clipboard(record, plugin_dir=None):
    kind = record["kind"]
    try:
        if plugin_dir:
            log(Path(plugin_dir), f"set clipboard start kind={kind}")
        if kind == "text":
            ok = _set_clipboard_data(CF_UNICODETEXT, _text_to_hglobal(record["content"] or ""))
        elif kind == "image":
            ok = _set_image(Path(record["image_path"]))
        elif kind == "files":
            ok = _set_clipboard_data(CF_HDROP, _bytes_to_hglobal(_build_hdrop(record.get("files") or [])))
        else:
            ok = False
        if plugin_dir:
            log(Path(plugin_dir), f"set clipboard done kind={kind} ok={ok}")
        return ok
    except Exception:
        if plugin_dir:
            log_exception(Path(plugin_dir), f"set clipboard failed kind={kind}")
        raise


def paste_record(record, plugin_dir=None):
    plugin_path = Path(plugin_dir) if plugin_dir else Path(__file__).resolve().parents[1]
    if set_record_to_clipboard(record, plugin_path):
        _spawn_delayed_paste(plugin_path)
    else:
        log(plugin_path, "paste skipped because clipboard write failed")


def _read_text():
    handle = user32.GetClipboardData(CF_UNICODETEXT)
    if not handle:
        return ""
    pointer = kernel32.GlobalLock(handle)
    if not pointer:
        return ""
    try:
        return ctypes.wstring_at(pointer)
    finally:
        kernel32.GlobalUnlock(handle)


def _read_files():
    handle = user32.GetClipboardData(CF_HDROP)
    if not handle:
        return []
    count = shell32.DragQueryFileW(handle, 0xFFFFFFFF, None, 0)
    paths = []
    for index in range(count):
        length = shell32.DragQueryFileW(handle, index, None, 0)
        buffer = ctypes.create_unicode_buffer(length + 1)
        shell32.DragQueryFileW(handle, index, buffer, length + 1)
        paths.append(buffer.value)
    return paths


def _set_image(path):
    if not path.is_absolute():
        path = Path(__file__).resolve().parents[1] / path
    image = Image.open(path).convert("RGB")
    return _set_clipboard_data(CF_DIB, _bytes_to_hglobal(_image_to_dib(image)))


def _set_clipboard_data(fmt, handle):
    if not handle:
        return False
    if not _open_clipboard():
        kernel32.GlobalFree(handle)
        return False
    try:
        user32.EmptyClipboard()
        if not user32.SetClipboardData(fmt, handle):
            kernel32.GlobalFree(handle)
            return False
        return True
    finally:
        user32.CloseClipboard()


def _text_to_hglobal(text):
    data = (text + "\0").encode("utf-16le")
    return _bytes_to_hglobal(data)


def _bytes_to_hglobal(data):
    handle = kernel32.GlobalAlloc(GMEM_MOVEABLE, len(data))
    if not handle:
        return None
    pointer = kernel32.GlobalLock(handle)
    if not pointer:
        kernel32.GlobalFree(handle)
        return None
    try:
        ctypes.memmove(pointer, data, len(data))
    finally:
        kernel32.GlobalUnlock(handle)
    return handle


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
    events = (INPUT * 4)(
        _keyboard_input(VK_CONTROL, 0),
        _keyboard_input(VK_V, 0),
        _keyboard_input(VK_V, KEYEVENTF_KEYUP),
        _keyboard_input(VK_CONTROL, KEYEVENTF_KEYUP),
    )
    sent = user32.SendInput(4, events, ctypes.sizeof(INPUT))
    if sent == 0:
        _send_ctrl_v_legacy()
    return sent


def _send_ctrl_v_legacy():
    user32.keybd_event(VK_CONTROL, 0, 0, 0)
    user32.keybd_event(VK_V, 0, 0, 0)
    user32.keybd_event(VK_V, 0, KEYEVENTF_KEYUP, 0)
    user32.keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, 0)


def _keyboard_input(vk, flags):
    return INPUT(INPUT_KEYBOARD, INPUT_UNION(ki=KEYBDINPUT(vk, 0, flags, 0, None)))


def _spawn_delayed_paste(plugin_dir):
    script = Path(__file__).resolve().parent / "delayed_paste.py"
    creationflags = 0
    if sys.platform == "win32":
        creationflags = subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS
    cmd = [sys.executable, str(script), "0.35", str(plugin_dir)]
    try:
        process = subprocess.Popen(
            cmd,
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            stdin=subprocess.DEVNULL,
            creationflags=creationflags
        )
        log(plugin_dir, f"delayed paste process started pid={process.pid} cmd={cmd!r}")
    except Exception:
        log_exception(plugin_dir, f"failed to start delayed paste process cmd={cmd!r}")
        raise


def _open_clipboard(retries=12, delay=0.04):
    for _ in range(retries):
        if user32.OpenClipboard(None):
            return True
        time.sleep(delay)
    return False


def _hash_text(text):
    return hashlib.sha256(text.encode("utf-8", errors="ignore")).hexdigest()


def _hash_image(image):
    import io

    output = io.BytesIO()
    image.save(output, "PNG")
    return hashlib.sha256(output.getvalue()).hexdigest()
