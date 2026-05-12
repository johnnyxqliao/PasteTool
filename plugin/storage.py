import json
import os
import shutil
import sqlite3
import time
from datetime import datetime
from pathlib import Path


class ClipboardStore:
    def __init__(self, plugin_dir: Path):
        self.plugin_dir = plugin_dir
        self.data_dir = plugin_dir / "Data"
        self.images_dir = self.data_dir / "images"
        self.db_path = self.data_dir / "history.sqlite3"
        self.data_dir.mkdir(exist_ok=True)
        self.images_dir.mkdir(exist_ok=True)
        self._init_db()

    def _connect(self):
        conn = sqlite3.connect(str(self.db_path), timeout=10)
        conn.row_factory = sqlite3.Row
        return conn

    def _init_db(self):
        with self._connect() as conn:
            conn.execute("""
                CREATE TABLE IF NOT EXISTS records (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    kind TEXT NOT NULL,
                    content TEXT,
                    image_path TEXT,
                    preview_path TEXT,
                    files_json TEXT,
                    source_app TEXT,
                    content_hash TEXT NOT NULL UNIQUE,
                    created_at INTEGER NOT NULL
                )
            """)
            conn.execute("CREATE INDEX IF NOT EXISTS idx_records_created_at ON records(created_at DESC)")
            conn.execute("CREATE INDEX IF NOT EXISTS idx_records_kind ON records(kind)")

    def add_text(self, text, source_app, content_hash):
        self._upsert("text", content_hash, source_app, content=text)

    def add_image(self, image_path, preview_path, source_app, content_hash):
        self._upsert("image", content_hash, source_app, image_path=image_path, preview_path=preview_path)

    def add_files(self, paths, source_app, content_hash):
        self._upsert("files", content_hash, source_app, files_json=json.dumps(paths, ensure_ascii=False))

    def _upsert(self, kind, content_hash, source_app, **fields):
        now = int(time.time())
        with self._connect() as conn:
            existing = conn.execute("SELECT id FROM records WHERE content_hash = ?", (content_hash,)).fetchone()
            if existing:
                conn.execute(
                    "UPDATE records SET created_at = ?, source_app = ? WHERE id = ?",
                    (now, source_app, existing["id"])
                )
                return

            conn.execute(
                """
                INSERT INTO records (kind, content, image_path, preview_path, files_json, source_app, content_hash, created_at)
                VALUES (?, ?, ?, ?, ?, ?, ?, ?)
                """,
                (
                    kind,
                    fields.get("content"),
                    fields.get("image_path"),
                    fields.get("preview_path"),
                    fields.get("files_json"),
                    source_app,
                    content_hash,
                    now
                )
            )

    def search(self, query, limit=80):
        query = (query or "").strip()
        with self._connect() as conn:
            if query:
                like = f"%{query}%"
                rows = conn.execute(
                    """
                    SELECT * FROM records
                    WHERE content LIKE ? OR files_json LIKE ?
                    ORDER BY created_at DESC
                    LIMIT ?
                    """,
                    (like, like, limit)
                ).fetchall()
            else:
                rows = conn.execute(
                    "SELECT * FROM records ORDER BY created_at DESC LIMIT ?",
                    (limit,)
                ).fetchall()
        return [self._format_row(row) for row in rows]

    def get(self, record_id):
        with self._connect() as conn:
            row = conn.execute("SELECT * FROM records WHERE id = ?", (record_id,)).fetchone()
        return self._format_row(row) if row else None

    def cleanup(self, keep_days):
        cutoff = int(time.time()) - int(keep_days) * 86400
        with self._connect() as conn:
            rows = conn.execute("SELECT * FROM records WHERE created_at < ?", (cutoff,)).fetchall()
            conn.execute("DELETE FROM records WHERE created_at < ?", (cutoff,))
        self._delete_image_files(rows)

    def clear(self):
        with self._connect() as conn:
            rows = conn.execute("SELECT * FROM records").fetchall()
            conn.execute("DELETE FROM records")
        self._delete_image_files(rows)

    def _delete_image_files(self, rows):
        for row in rows:
            for key in ("image_path", "preview_path"):
                value = row[key]
                if not value:
                    continue
                try:
                    path = self.plugin_dir / value
                    if path.exists() and self.images_dir in path.parents:
                        path.unlink()
                except Exception:
                    pass
        if self.images_dir.exists() and not any(self.images_dir.iterdir()):
            shutil.rmtree(self.images_dir, ignore_errors=True)
            self.images_dir.mkdir(exist_ok=True)

    def _format_row(self, row):
        record = dict(row)
        created = datetime.fromtimestamp(record["created_at"])
        record["created_display"] = created.strftime("%Y/%m/%d %H:%M:%S")

        if record["kind"] == "text":
            text = record["content"] or ""
            record["title"] = _single_line(text, 100)
            record["subtitle"] = f'{record["created_display"]} · {record["source_app"] or "未知来源"}'
        elif record["kind"] == "image":
            record["title"] = "图片"
            record["subtitle"] = f'{record["created_display"]} · {record["source_app"] or "未知来源"}'
        else:
            files = json.loads(record["files_json"] or "[]")
            record["files"] = files
            first = files[0] if files else ""
            name = os.path.basename(first) if first else "文件"
            suffix = f" 等 {len(files)} 个文件" if len(files) > 1 else ""
            record["title"] = f"文件：{name}{suffix}"
            record["subtitle"] = first
        return record


def _single_line(text, limit):
    value = " ".join(str(text).replace("\r", " ").replace("\n", " ").split())
    if len(value) <= limit:
        return value or "(空文本)"
    return value[:limit - 1] + "..."
