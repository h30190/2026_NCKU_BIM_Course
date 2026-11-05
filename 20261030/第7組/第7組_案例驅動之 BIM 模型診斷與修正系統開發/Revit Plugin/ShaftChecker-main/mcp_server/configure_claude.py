"""
把 ShaftChecker MCP 伺服器加入 Claude 桌面版設定檔（claude_desktop_config.json）。
- 只新增/更新 "shaft-checker" 這一項，其他既有設定（例如 revit-mcp）完全保留。
- 修改前會備份成 claude_desktop_config.json.bak。
- 使用執行本腳本的 Python 路徑，確保與安裝 mcp 套件的是同一個 Python。
"""

import glob
import json
import os
import shutil
import sys

SERVER_NAME = "shaft-checker"
here = os.path.dirname(os.path.abspath(__file__))
server_py = os.path.join(here, "server.py")

appdata = os.environ.get("APPDATA", "")
localappdata = os.environ.get("LOCALAPPDATA", "")

# 一般安裝與 Microsoft Store (MSIX) 安裝的設定檔位置
candidates = [os.path.join(appdata, "Claude", "claude_desktop_config.json")]
candidates += glob.glob(os.path.join(localappdata, "Packages", "Claude_*", "LocalCache", "Roaming", "Claude",
                                     "claude_desktop_config.json"))
existing = [p for p in candidates if os.path.exists(p)]
targets = existing or [candidates[0]]

entry = {"command": sys.executable, "args": [server_py]}

for path in targets:
    os.makedirs(os.path.dirname(path), exist_ok=True)
    config = {}
    if os.path.exists(path):
        shutil.copyfile(path, path + ".bak")
        with open(path, "r", encoding="utf-8") as f:
            text = f.read().strip()
            config = json.loads(text) if text else {}
    config.setdefault("mcpServers", {})[SERVER_NAME] = entry
    with open(path, "w", encoding="utf-8") as f:
        json.dump(config, f, ensure_ascii=False, indent=2)
    print("已更新：", path)

print("伺服器：", server_py)
print("Python：", sys.executable)
