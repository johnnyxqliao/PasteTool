import json
from pathlib import Path


class Settings:
    def __init__(self, plugin_dir: Path):
        self.path = plugin_dir / "Data" / "settings.json"
        self.keep_days = 14
        self.max_results = 80
        self.load()

    def load(self):
        if not self.path.exists():
            return
        try:
            data = json.loads(self.path.read_text(encoding="utf-8"))
        except Exception:
            return
        self.keep_days = int(data.get("keep_days", self.keep_days))
        self.max_results = int(data.get("max_results", self.max_results))

    def save(self):
        self.path.parent.mkdir(exist_ok=True)
        self.path.write_text(
            json.dumps({
                "keep_days": self.keep_days,
                "max_results": self.max_results
            }, ensure_ascii=False, indent=2),
            encoding="utf-8"
        )
