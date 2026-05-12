import base64
from pathlib import Path


PNG_1X1 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+/p9sAAAAASUVORK5CYII="


def ensure_icons(plugin_dir: Path):
    images = plugin_dir / "Images"
    images.mkdir(exist_ok=True)
    payload = base64.b64decode(PNG_1X1)
    for name in ("app.png", "text.png", "image.png", "file.png", "copy.png", "paste.png"):
        path = images / name
        if not path.exists():
            path.write_bytes(payload)
