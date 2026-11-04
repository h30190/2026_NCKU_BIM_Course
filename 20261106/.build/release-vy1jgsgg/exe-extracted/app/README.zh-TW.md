# 案例驅動之 BIM 模型診斷與修正系統開發

[English](README.md)

Revit 頁籤：**案例驅動 BIM 診斷與修正**

版本：**2026.11.04.2 · Windows x64 · Revit 2022**

## 安裝

1. 先安裝 Autodesk Revit 2022，以及包含 CLI、已連接 Revit 2022 的相容 **pyRevit**，確認 Revit 內可見 pyRevit 頁籤。
2. 儲存模型並關閉 Revit。
3. 雙擊 **CaseDrivenBIM-2026.11.04.2-Revit2022-Setup.exe**，按 **安裝 / Install**。
4. 安裝程式會檢查檔案、安裝獨立 Python 與套件、測試獨立後端，並註冊外掛。
5. 開啟 Revit 2022，選擇 **案例驅動 BIM 診斷與修正 → 輔助工具 → 開啟面板**。

安裝可離線完成，不需要管理員權限或另裝 Python。Revit 和 pyRevit 是前置條件，未包含於安裝包。本包只包含 **Revit 2022** 管道間引擎；其他版本須另行編譯與驗證。安裝檔未作數位簽章，Windows 可能顯示發行者提示；發行檔附有 SHA-256 校驗碼。

預設安裝位置：`%LOCALAPPDATA%\Programs\CaseDrivenBIM\app`。開始功能表的 **Case-driven BIM** 資料夾提供中英文說明及 **Check Installation**。若曾註冊開發版，請先在 pyRevit 設定移除舊副本的搜尋路徑，避免同時載入兩份。

## 主要功能

| 頁籤區塊 | 功能 |
| --- | --- |
| 模型檢查 | 重複元素、重疊、碰撞、標記及 Revit 警告 |
| 法規檢查 | 管道間防火區劃、維修門與空間、管線分隔與間距 |
| 問題與解法 | 問題紀錄、解法審查、案例／AI 建議及修正 |
| 輔助工具 | 相似元素、元素比較、關係圖及案例庫 |

灰色法規按鈕是預留項目；該區塊目前只有 **管道間檢查** 可執行。

### 模型檢查及問題紀錄

開啟 **建模檢查**，選擇範圍及項目後執行。結果顯示兩個元素時，**定位** 會選取 **後者／右邊元素**。將結果帶入 **問題紀錄**，核對後儲存草稿或提交。在 **解法審查** 記錄及驗證解法，之後才作為已驗證案例重用。

### 管道間檢查及 CSV

開啟 **管道間檢查 → 檢查主模型及已載入連結**。選取結果可定位、帶入問題紀錄或開啟修改建議。按 **匯出問題 CSV**，選擇儲存位置，即可匯出本次所有違規、警告及資料不足結果。一項問題一行，同一管道間有多個問題會分開保存。

欄位包含模型來源、房間／樓層、規則、實測與要求值、建議及相關元素 ID。UTF-8 BOM 讓 Excel 正確顯示中文。來源無法對應及範圍限制會保留註記；通過項目不會匯出。模型變更或切換文件後必須重新檢查。連結元素保留實際來源 ID；定位會選取主模型中的連結實例。

規則設定：`ShaftChecker-main/src/ShaftChecker.Revit/config/shaft-rules.json`。請核對規則是否符合專案要求。

### 修正建議

固定目標並選取相關 Revit 元素，在 **修改建議** 選擇 **透過過去案例** 或 **透過 AI 建議**。每項建議有獨立 **套用** 按鈕；**套用全部** 跳過已完成項目。**自動修正** 會產生及套用方案。

修正只限目標與目前選取物。一組配對已修正，不會令共用同一元素的另一組配對變成已修正。若失敗或結果未能確定，後續修改停止；先檢查模型再重新產生建議。

## 選用：Claude／Revit MCP

模型檢查、問題紀錄及 CSV 不需要 AI 帳戶。AI 建議及 MCP 修正需要自己的 Claude CLI 登入、Node.js 及相容的 Revit MCP 外掛／伺服器。這些程式與帳戶資料未隨包提供。

1. 安裝 Claude CLI 並登入自己的帳戶。
2. 安裝及設定 Revit MCP 外掛與 Node 伺服器，在 Revit 啟用服務。
3. 將安裝目錄的 `.mcp.example.json` 複製成 `.mcp.json`，改成自己的伺服器路徑。也可在啟動 Revit 前，以 `CASEGRAPH_MCP_CONFIG` 指向自己的 JSON 設定。
4. 產生或套用建議前，釋放其他客戶端佔用的 MCP 連線。

選用變數：`CASEGRAPH_CLAUDE`（CLI 路徑）、`CASEGRAPH_CLAUDE_MODEL`（模型）、`CASEGRAPH_MCP_CONFIG`。獨立分析功能可設定 `CASEGRAPH_LLM_URL`、`CASEGRAPH_LLM_MODEL`、`CASEGRAPH_LLM_KEY`。不提供 API 密鑰；不要分享已設定的 MCP／環境檔或登入資料。AI 功能會將相關模型證據傳送至你設定的服務商。

## 資料、更新及解除安裝

使用後，案例及日誌會儲存在本機 `backend/data`；後端資料位置可由 `CASEBASE_DATA_DIR` 覆寫。安裝包沒有資料庫、模型、截圖、個人日誌、帳戶設定或 API 密鑰。第三方套件的公開授權及作者署名會保留。

更新前關閉 Revit 和已安裝的後端，再執行安裝檔。更新保留案例、本機 MCP 設定及管道間規則；舊程式留在同層 `previous-*` 資料夾以供還原。不會自動搬移另一個開發工作區的資料；如需搬移，先備份，再於兩邊服務均停止時複製該工作區的 `backend/data`。

在 Windows **已安裝的應用程式 → Case-driven BIM Diagnosis and Repair → 解除安裝**。資料及設定留在同層 `saved-data-*`，舊版本備份亦會保留；確定不再需要後才自行刪除備份。

## 疑難排解

| 情況 | 處理方法 |
| --- | --- |
| 沒有頁籤／仍是舊介面 | 確認 pyRevit 已連接 Revit 2022，移除重複搜尋路徑並重啟 Revit |
| 後端無法啟動 | 執行開始功能表的 **Check Installation**；查看 `backend/data/server.log`，確認 8000 連接埠可用 |
| 手動啟動後端 | 在安裝目錄執行 `powershell -ExecutionPolicy Bypass -File Start-CaseGraph.ps1` |
| Claude／MCP 409 | 在 Revit MCP 設定釋放其他客戶端連線，再重試 |
| 匯出按鈕停用 | 重新檢查管道間；沒有問題的結果不提供問題匯出 |
| 缺少管道間 DLL | 重裝 Revit 2022 安裝包；其他版本需要相應編譯 |

健康檢查：`http://127.0.0.1:8000/graph/health`，服務只監聽本機。**Check Installation** 使用臨時資料庫，不呼叫 AI 或修改 Revit。自動 UI 測試使用模擬文件；仍須在自己的模型驗證實際幾何及修正。

## 開發及打包

把 `CaseBase.extension`、`backend`、`shared`、`ShaftChecker-main` 放在同一資料夾。內部套件名稱為相容性而保留。

```powershell
python -m venv backend/.venv
backend/.venv/Scripts/python -m pip install -r backend/requirements-dev.txt
./tools/Build-ShaftChecker.ps1 -RevitVersion 2022
Set-Location backend
./.venv/Scripts/python -m pytest tests -q
Set-Location ..
powershell -NoProfile -STA -ExecutionPolicy Bypass -File backend/tests/run_ironpython_encoding.ps1 -Suite panel
backend/.venv/Scripts/python tools/build_release.py
```

打包工具使用固定執行環境、套件雜湊及指定檔案清單，下載缺少的建置材料，驗證與掃描內容後，把安裝檔、校驗碼及檢查清單放入 `dist`。確切版本與雜湊見 `installer/release-lock.json`。Python 來自[官方 3.13.17 發行頁](https://www.python.org/downloads/release/python-31317/)。
