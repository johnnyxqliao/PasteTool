from datetime import datetime
from pathlib import Path
import traceback


def log(plugin_dir: Path, message: str):
    try:
        data_dir = plugin_dir / "Data"
        data_dir.mkdir(exist_ok=True)
        line = f"{datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]} {message}\n"
        with (data_dir / "paste_tool.log").open("a", encoding="utf-8") as handle:
            handle.write(line)
    except Exception:
        pass


def log_exception(plugin_dir: Path, message: str):
    try:
        log(plugin_dir, f"{message}\n{traceback.format_exc()}")
    except Exception:
        pass
