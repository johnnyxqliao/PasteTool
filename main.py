# -*- coding: utf-8 -*-

import os
import subprocess
import sys
import ctypes
from pathlib import Path

PLUGIN_DIR = Path.absolute(Path(__file__).parent)
for rel in (".", "lib", "plugin"):
    sys.path.insert(0, str(PLUGIN_DIR / rel))

from flowlauncher import FlowLauncher

from plugin.clipboard_bridge import paste_record, set_record_to_clipboard
from plugin.icons import ensure_icons
from plugin.settings import Settings
from plugin.storage import ClipboardStore


class PasteTool(FlowLauncher):
    def __init__(self):
        ensure_icons(PLUGIN_DIR)
        self.store = ClipboardStore(PLUGIN_DIR)
        self.settings = Settings(PLUGIN_DIR)
        super().__init__()

    def query(self, query):
        self._ensure_monitor()
        self.store.cleanup(self.settings.keep_days)

        text = (query or "").strip()
        lower = text.lower()

        if lower in ("clear", "清空"):
            return [self._command_result("清空所有剪贴板历史", "按 Enter 删除文本记录、图片缓存和文件记录", "clear_history")]

        if lower in ("settings", "setting", "设置"):
            return self._settings_results()

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
        record = self.store.get(int(record_id))
        if record:
            paste_record(record)

    def copy_only(self, record_id):
        record = self.store.get(int(record_id))
        if record:
            set_record_to_clipboard(record)

    def clear_history(self):
        self.store.clear()

    def set_keep_days(self, days):
        self.settings.keep_days = int(days)
        self.settings.save()
        self.store.cleanup(self.settings.keep_days)

    def _record_result(self, record, index):
        kind = record["kind"]
        title = record["title"]
        icon = "Images\\text.png"

        if kind == "image":
            icon = record["preview_path"] or "Images\\image.png"
            title = "图片"
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
        pid_file = PLUGIN_DIR / "Data" / "monitor.pid"
        if pid_file.exists():
            try:
                pid = int(pid_file.read_text(encoding="utf-8").strip())
                if self._is_process_alive(pid):
                    return
            except Exception:
                pass

        data_dir = PLUGIN_DIR / "Data"
        data_dir.mkdir(exist_ok=True)
        creationflags = 0
        if os.name == "nt":
            creationflags = subprocess.CREATE_NO_WINDOW | subprocess.DETACHED_PROCESS

        subprocess.Popen(
            [sys.executable, str(PLUGIN_DIR / "plugin" / "monitor.py"), str(PLUGIN_DIR)],
            cwd=str(PLUGIN_DIR),
            stdout=subprocess.DEVNULL,
            stderr=subprocess.DEVNULL,
            stdin=subprocess.DEVNULL,
            creationflags=creationflags
        )

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
