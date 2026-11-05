"""
產生網頁儀表板的「假資料預覽頁」，不用開 Revit 就能修改、檢查介面。

用法（在專案根目錄）：
    python tools/ui-preview/build_preview.py
產出：
    tools/ui-preview/output/ui-preview.html（雙擊用瀏覽器開啟）

原理：把 mock-shim.html（假資料＋攔截 fetch 的小程式）插到 ui/index.html 的主程式前面。
正式的 index.html 不會被修改；預覽頁不要提交到 Git（已列在 .gitignore）。
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
INDEX = os.path.join(ROOT, "src", "ShaftChecker.Revit", "ui", "index.html")
SHIM = os.path.join(HERE, "mock-shim.html")
OUT_DIR = os.path.join(HERE, "output")
OUT = os.path.join(OUT_DIR, "ui-preview.html")
MARKER = "\n<script>\n(() =>"

with open(INDEX, encoding="utf-8") as f:
    page = f.read()
with open(SHIM, encoding="utf-8") as f:
    shim = f.read()

i = page.find(MARKER)
if i < 0:
    sys.exit("找不到 index.html 的主程式開頭（<script> 後接 (() =>），請確認檔案結構沒有被改掉。")

os.makedirs(OUT_DIR, exist_ok=True)
with open(OUT, "w", encoding="utf-8") as f:
    f.write(page[:i] + "\n" + shim + page[i:])
print("已產生：" + OUT)
