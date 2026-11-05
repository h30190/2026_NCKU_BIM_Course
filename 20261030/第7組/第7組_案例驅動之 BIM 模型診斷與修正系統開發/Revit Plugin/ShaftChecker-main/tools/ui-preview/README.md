# 儀表板假資料預覽

- `mock-shim.html`：9 個假管道間（含違規、資料不足、連結模型、地下層、梁穿越、穿梁、瓦斯管）、疑似管道間與辨識結果，並攔截 `/check`、`/select` 等 API 呼叫。
- `build_preview.py`：把假資料插進正式的 `ui/index.html`，產生 `output/ui-preview.html`。

修改介面的流程：
1. 改 `src/ShaftChecker.Revit/ui/index.html`
2. `python tools/ui-preview/build_preview.py`
3. 用瀏覽器開 `tools/ui-preview/output/ui-preview.html` 檢查（不同視窗寬度、側欄拖曳、點選各分組）
4. 確認後再用 `build-and-install.bat` 部署，到 Revit 實際測一次

假資料的 API 欄位要跟 `ShaftCheckService.cs` 回傳的一致；後端新增欄位時，記得也更新 `mock-shim.html`。
