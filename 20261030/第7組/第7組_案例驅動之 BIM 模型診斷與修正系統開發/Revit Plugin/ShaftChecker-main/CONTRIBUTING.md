# 組員開發指南

這份文件說明怎麼加入開發、每天怎麼同步、怎麼提交修改。專案說明與架構請看 [README.md](README.md)。

## 一、第一次加入

1. **安裝環境**
   - Revit 2024
   - [.NET 8 SDK 以上](https://dotnet.microsoft.com/download)（安裝完在命令提示字元輸入 `dotnet --list-sdks` 確認）
   - [GitHub Desktop](https://desktop.github.com/)（不熟 Git 的組員用這個就好）
   - 要用 AI 對話的人：[Python 3.10 以上](https://www.python.org/downloads/)（安裝時勾選 Add python.exe to PATH）、Claude 桌面版
   - 建議：Visual Studio 2022（勾「.NET 桌面開發」）或 VS Code
2. **取得程式**：GitHub Desktop → File → Clone repository → 選 `ShaftChecker`。
   本機路徑請選**不會被雲端同步的資料夾**，例如 `C:\dev\ShaftChecker`。
   放在 OneDrive、Google Drive、Dropbox 裡，同步程式會跟 Git 搶著改檔案，容易壞掉。
3. **建置**：關閉 Revit，雙擊 `build-and-install.bat`，看到「單元測試全部通過」「建置成功」即可。
4. **AI 對話（選用）**：雙擊 `setup-mcp.bat`，再完全結束 Claude 桌面版（系統匣圖示按右鍵 → 結束）後重開。
   專案資料夾搬家後要再執行一次，因為 Claude 記的是 `server.py` 的完整路徑。
5. **測試模型**：從 README〈測試模型〉的共用雲端下載，放在專案資料夾**以外**的地方。

## 二、每天的流程

### 用 GitHub Desktop

1. **開工先同步**：左上切到 `main` → 按 **Fetch origin** → 若出現 **Pull origin** 就按。
2. **開新分支**：Current branch → **New branch**，依下方規則命名，Create branch based on `main`。
3. **寫程式、測試**：改完先跑 `build-and-install.bat`，確認測試通過、Revit 裡實際能用。
4. **提交（Commit）**：左下 Summary 寫一句話說明，按 **Commit to 分支名稱**。可以分好幾次提交。
5. **推上 GitHub**：按 **Push origin**（第一次是 Publish branch）。
6. **開 Pull Request**：按 **Create Pull Request**，在網頁上填範本、指定一位組員 Review。
7. **合併後**：切回 `main` → Pull origin，刪掉已合併的分支，再從第 1 步開始下一個工作。

### 用指令（同樣的流程）

```
git switch main
git pull
git switch -c feature/p6-pipe-slope
# ……修改、測試……
git add -A
git commit -m "P6：新增管線坡度檢討"
git push -u origin feature/p6-pipe-slope
# 到 GitHub 網頁開 Pull Request
```

## 三、命名規則

**分支**：`類型/簡短英文說明`

| 類型 | 用途 | 例子 |
| --- | --- | --- |
| `feature/` | 新功能、新規則 | `feature/p6-pipe-slope`、`feature/dashboard-export-pdf` |
| `fix/` | 修正錯誤 | `fix/locate-link-room` |
| `ui/` | 只改儀表板外觀 | `ui/kanban-spacing` |
| `docs/` | 文件、報告 | `docs/midterm-report` |
| `test/` | 只補測試或測試模型說明 | `test/s2-edge-cases` |
| `ci/` | 自動測試、GitHub 設定 | `ci/github-actions` |

**Commit 訊息**：中文一句話，開頭寫影響的規則或模組，例如：
- `A7：沒有淨高參數時以門框寬度推算`
- `儀表板：分組檢視改成母子項目`
- `修正：連結模型的管道間無法定位`

## 四、Pull Request 與審查

- `main` 永遠要能建置、測試全過；**不要直接推到 main**（組長可在 GitHub 的 Settings → Branches 開啟保護）。
- 一個 PR 只做一件事，越小越好審。
- PR 範本的「自我檢查」逐項勾完；改介面要附截圖。
- 審查的人至少要：讀過改動、在自己電腦建置一次；有疑問就在 PR 留言，不要直接幫忙改。
- 合併方式用 **Squash and merge**，`main` 的歷史會比較乾淨。

### 自動測試（GitHub Actions）

每次開 PR 或推新的 Commit 到 PR，GitHub 會自動跑兩項檢查（設定在 `.github/workflows/test.yml`）：

| 檢查 | 內容 | 大約時間 |
| --- | --- | --- |
| 單元測試 | `tests/` 全部測試（不需要 Revit） | 1–2 分鐘 |
| 建置 Revit 2024 外掛 | 確認 `ShaftChecker.Revit` 能編譯（例如沒用到 C# 7.3 以後的語法） | 2–4 分鐘 |

- PR 頁面下方會顯示結果：綠色 ✓ 通過、紅色 ✗ 失敗。**紅色的 PR 不要合併**。
- 失敗時按該項的 **Details** 看紀錄；單元測試失敗會另外附上 `test-results` 檔案，在 Actions 頁面的 Artifacts 下載。
- 在自己電腦重現：`dotnet test tests\ShaftChecker.Tests\ShaftChecker.Tests.csproj`。
- 組長設定（只要做一次）：Settings → Branches → `main` 的規則 → 勾 **Require status checks to pass before merging**，
  搜尋並加入「單元測試」「建置 Revit 2024 外掛」兩項。之後檢查沒過，GitHub 就不讓合併。

## 五、分工與容易衝突的檔案

依程式分層分工，彼此不太會改到同一個檔案（各資料夾的內容見 README〈資料夾與分工〉）：

| 角色 | 主要範圍 |
| --- | --- |
| 規則與測試 | `src/ShaftChecker.Core/`、`tests/` |
| Revit 擷取與定位 | `src/ShaftChecker.Revit/Collectors/`、`Services/`、`Bridge/` |
| 網頁儀表板 | `src/ShaftChecker.Revit/ui/index.html`、`tools/ui-preview/` |
| AI／MCP | `mcp_server/` |
| 文件、測試模型、報告 | `README.md`、`docs/`、共用雲端的模型 |

**容易衝突的檔案**，改之前先在群組說一聲，同一時間只讓一個人改：

- `src/ShaftChecker.Revit/ui/index.html`（一千多行的單一檔案）
- `src/ShaftChecker.Revit/config/shaft-rules.json`
- `src/ShaftChecker.Core/Config/RuleConfig.cs`
- `src/ShaftChecker.Revit/Services/ShaftCheckService.cs`
- `README.md`

## 六、常見開發任務

### 新增一條檢討規則（例如 P6）

1. `src/ShaftChecker.Core/Rules/` 新增 `P6_xxx.cs`，繼承 `RuleBase`，設定 `Id`、`Name`、`Nature`（法規／設計參數）、`Reference`。
   法規條文寫在 `RuleGroups.cs` 的 `LegalRefs`，**引用前先查證條號與條文**。
2. 加到 `RuleEngine.AllRules`。新的編號開頭（不是 A、P、S）要在 `RuleGroups.Of` 指定分組。
3. 需要新門檻時，`RuleConfig.cs` 要改五個地方：欄位、`SetDefaults`、`OverridableKeys`、`ApplyOverride`、`Thresholds`。
   `shaft-rules.json` 也要加同名欄位，並加進 `enabledRules`。
4. `ShaftCheckService.ThresholdKeysOf` 加上這條規則用到的門檻。
5. `tests/` 補單元測試：至少一個合格、一個違規、一個資料不足（如果有）。
6. 儀表板 `ui/index.html`：`RULE_NAMES` 加名稱；有門檻就在 `SLIDERS` 加滑桿。
7. `mcp_server/server.py`：INSTRUCTIONS 的〈規則一覽〉、`check_shafts` 的 overrides 說明。
8. README〈檢討項目〉表格加一列。

### 只改儀表板

1. 改 `src/ShaftChecker.Revit/ui/index.html`。
2. `python tools/ui-preview/build_preview.py`，用瀏覽器開 `tools/ui-preview/output/ui-preview.html` 檢查：
   拉窄、拉寬視窗，拖曳側欄，點每個分組卡。
3. `build-and-install.bat` 部署後，到 Revit 實際點一次「定位」與違規項目。

### 新增 API 端點或 MCP 工具

1. `ShaftCheckService.cs` 寫方法（只能在 Revit 主執行緒呼叫 Revit API，Bridge 已經處理好）。
2. `HttpBridge.Route` 加路徑，錯誤訊息裡的可用端點清單也要更新。
3. 要給 AI 用的話，`server.py` 加 `@mcp.tool()`，docstring 用中文寫清楚參數與回傳。
4. 改完 `server.py` 要完全重開 Claude 桌面版才會生效。

## 七、遇到衝突（Conflict）怎麼辦

1. 先把自己的修改 Commit。
2. GitHub Desktop：Branch → **Update from main**；或指令 `git merge main`。
3. 衝突的檔案會標出 `<<<<<<<`、`=======`、`>>>>>>>`，保留正確的內容、刪掉這三種標記。
4. 重新建置、測試通過後再 Commit、Push。
5. `index.html` 這種大檔案衝突很多時，直接找另一位改的人一起處理，不要自己猜。

## 八、不要提交的東西

已經寫在 `.gitignore`，但 Commit 前還是看一下變更清單：

- Revit 模型 `.rvt` 與備份（`.0001.rvt`）
- `bin/`、`obj/` 編譯產物
- `build-log.txt`、`setup-mcp-log.txt`、匯出的 CSV
- 壓縮檔
- 個人電腦的路徑或帳號：例如把 `launchSettings.json` 改成自己的 Revit 路徑，這種修改不要提交

不小心提交了大檔案，先不要再 Push，在群組說，由組長處理。
