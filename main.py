# -*- coding: utf-8 -*-

import os
import subprocess
import sys
import ctypes
import time
from pathlib import Path

PLUGIN_DIR = Path.absolute(Path(__file__).parent)
for rel in (".", "lib", "plugin"):
    sys.path.insert(0, str(PLUGIN_DIR / rel))

from flowlauncher import FlowLauncher

from plugin.clipboard_bridge import paste_record, set_record_to_clipboard
from plugin.icons import ensure_icons
from plugin.logger import log, log_exception
from plugin.settings import Settings
from plugin.storage import ClipboardStore


def _kind_label(kind):
    if kind == "image":
        return "【Image】"
    if kind == "files":
        return "【File】"
    return "【Text】"


class PasteTool(FlowLauncher):
    def __init__(self):
        ensure_icons(PLUGIN_DIR)
        self.store = ClipboardStore(PLUGIN_DIR)
        self.settings = Settings(PLUGIN_DIR)
        super().__init__()

    def init(self):
        log(PLUGIN_DIR, "init requested")
        self._ensure_monitor()

    def query(self, query):
        self._ensure_monitor()
        self.store.cleanup(self.settings.keep_days)

        text = (query or "").strip()
        lower = text.lower()

        if lower in ("clear", "清空"):
            return [self._command_result("清空所有剪贴板历史", "按 Enter 删除文本记录、图片缓存和文件记录", "clear_history")]

        if lower in ("settings", "setting", "设置"):
            return self._settings_results()

        if lower in ("status",):
            return self._status_results()

        if lower.startswith("keep "):
            return self._keep_results(lower)

        records = self.store.search(text, limit=self.settings.max_results)
        if not records:
            subtitle = "正在记录剪贴板。复制文本、图片或文件后会显示在这里。"
            if text:
                subtitle = "没有匹配的剪贴板历史。"
            return [{
                "Title": "暂无剪贴板历史",
                "SubTitle": subtitle,
                "IcoPath": "Images\\app.png",
                "Score": 10
            }]

        return [self._record_result(record, index) for index, record in enumerate(records)]

    def context_menu(self, data):
        if not data:
            return []
        record_id = int(data[0])
        return [
            {
                "Title": "只复制到剪贴板",
                "SubTitle": "不执行粘贴动作",
                "IcoPath": "Images\\copy.png",
                "JsonRPCAction": {
                    "method": "copy_only",
                    "parameters": [record_id]
                }
            },
            {
                "Title": "复制并粘贴",
                "SubTitle": "恢复这条记录并发送 Ctrl+V",
                "IcoPath": "Images\\paste.png",
                "JsonRPCAction": {
                    "method": "paste",
                    "parameters": [record_id]
                }
            }
        ]

    def paste(self, record_id):
        try:
            log(PLUGIN_DIR, f"paste action requested record_id={record_id}")
            record = self.store.get(int(record_id))
            if record:
                log(PLUGIN_DIR, f"paste action found kind={record['kind']} title={record.get('title')!r}")
                paste_record(record, PLUGIN_DIR)
            else:
                log(PLUGIN_DIR, f"paste action record not found record_id={record_id}")
        except Exception:
            log_exception(PLUGIN_DIR, f"paste action failed record_id={record_id}")

    def copy_only(self, record_id):
        try:
            log(PLUGIN_DIR, f"copy_only action requested record_id={record_id}")
            record = self.store.get(int(record_id))
            if record:
                log(PLUGIN_DIR, f"copy_only action found kind={record['kind']} title={record.get('title')!r}")
                set_record_to_clipboard(record, PLUGIN_DIR)
            else:
                log(PLUGIN_DIR, f"copy_only action record not found record_id={record_id}")
        except Exception:
            log_exception(PLUGIN_DIR, f"copy_only action failed record_id={record_id}")

    def clear_history(self):
        log(PLUGIN_DIR, "clear_history action requested")
        self.store.clear()

    def set_keep_days(self, days):
        self.settings.keep_days = int(days)
        self.settings.save()
        self.store.cleanup(self.settings.keep_days)

    def _record_result(self, record, index):
        kind = record["kind"]
        title = f'{_kind_label(kind)} {record["title"]}'
        icon = "Images\\text.png"

        if kind == "image":
            icon = record["preview_path"] or "Images\\image.png"
        elif kind == "files":
            icon = "Images\\file.png"

        subtitle = f'{record["created_display"]} · {record["source_app"] or "未知来源"}'
        if kind == "files":
            subtitle = record["subtitle"]

        return {
            "Title": title,
            "SubTitle": subtitle,
            "IcoPath": icon,
            "ContextData": [record["id"]],
            "JsonRPCAction": {
                "method": "paste",
                "parameters": [record["id"]]
            },
            "Score": max(1, 1000 - index)
        }

    def _settings_results(self):
        return [
            self._command_result(f"当前保留时间：{self.settings.keep_days} 天", "输入 c keep 7 / c keep 14 / c keep 30 修改", "set_keep_days", self.settings.keep_days),
            self._command_result("保留 7 天", "按 Enter 应用", "set_keep_days", 7),
            self._command_result("保留 14 天", "按 Enter 应用", "set_keep_days", 14),
            self._command_result("保留 30 天", "按 Enter 应用", "set_keep_days", 30),
        ]

    def _keep_results(self, lower):
        parts = lower.split()
        if len(parts) == 2 and parts[1].isdigit():
            days = int(parts[1])
            if days in (7, 14, 30):
                return [self._command_result(f"设置保留 {days} 天", "按 Enter 应用并清理过期记录", "set_keep_days", days)]
        return self._settings_results()

    def _status_results(self):
        running, detail = self._monitor_status()
        stats = self.store.stats()
        title = "Monitor running" if running else "Monitor not healthy"
        latest = stats["latest"] or "none"
        return [
            self._command_result(
                title,
                f"{detail} · records={stats['total']} · latest={latest}",
                "noop"
            )
        ]

    def noop(self):
        pass

    def _command_result(self, title, subtitle, method, *params):
        return {
            "Title": title,
            "SubTitle": subtitle,
            "IcoPath": "Images\\app.png",
            "JsonRPCAction": {
                "method": method,
                "parameters": list(params)
            },
            "Score": 1000
        }

    def _ensure_monitor(self):
        running, detail = self._monitor_status()
        if running:
            return
        log(PLUGIN_DIR, f"monitor not healthy, starting new monitor: {detail}")
        data_dir = PLUGIN_DIR / "Data"
        data_dir.mkdir(exist_ok=True)
        creationflags = 0
        if os.name == "nt":
            creationflags = subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS

        process = subprocess.Popen(
            [sys.executable, str(PLUGIN_DIR / "plugin" / "monitor.py"), str(PLUGIN_DIR)],
            cwd=str(PLUGIN_DIR),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            stdin=subprocess.DEVNULL,
            creationflags=creationflags
        )
        log(PLUGIN_DIR, f"monitor process launched pid={process.pid}")

    def _monitor_status(self):
        pid_file = PLUGIN_DIR / "Data" / "monitor.pid"
        heartbeat_file = PLUGIN_DIR / "Data" / "monitor.heartbeat"
        if not pid_file.exists():
            return False, "no monitor.pid"
        try:
            pid = int(pid_file.read_text(encoding="utf-8").strip())
        except Exception:
            return False, "monitor.pid unreadable"

        if not self._is_process_alive(pid):
            return False, f"pid={pid} is not running"

        if not heartbeat_file.exists():
            return False, f"pid={pid} running but heartbeat is missing"

        try:
            lines = heartbeat_file.read_text(encoding="utf-8").splitlines()
            heartbeat_at = float(lines[0])
            sequence = lines[2] if len(lines) > 2 else ""
            last_kind = lines[3] if len(lines) > 3 else ""
            age = time.time() - heartbeat_at
            if age > 8:
                return False, f"pid={pid} heartbeat stale age={age:.1f}s"
            suffix = f" · last_kind={last_kind}" if last_kind else ""
            return True, f"pid={pid} · heartbeat={age:.1f}s · sequence={sequence}{suffix}"
        except Exception:
            return False, f"pid={pid} heartbeat unreadable"

    def _is_process_alive(self, pid):
        if os.name != "nt":
            try:
                os.kill(pid, 0)
                return True
            except OSError:
                return False

        process_query_limited_information = 0x1000
        handle = ctypes.windll.kernel32.OpenProcess(process_query_limited_information, False, int(pid))
        if not handle:
            return False
        ctypes.windll.kernel32.CloseHandle(handle)
        return True


if __name__ == "__main__":
    PasteTool()
