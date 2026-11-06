# Window Agent API — Revit AI 通風開窗

依建築所在地的氣象資料推薦開窗方案，經使用者確認後透過 Revit MCP 在模型中建窗、回讀驗證，並匯出最終檔案。

> **GitHub 上的版本不含 `.rvt`**（課程規定不 commit Revit 模型）。`revit_models/model_01.rvt`、`model_02.rvt` 與 `examples/` 裡建好窗的 rvt，請向第 2 組索取完整資料包 `window_agent_api_public_v1.1_20261030.zip`（雲端分享），解壓後覆蓋到這個資料夾即可。不連 Revit 的功能（氣象分析、產生方案、評分、室內風流、手動調整）不需要 rvt 也能使用。

```
1 模型 → 2 載入窗型 → 3 依氣象選方案 → 4 確認後建窗 → 5 最終檔案
```

## 需求

- Node.js 22 以上（使用內建 WebSocket，不需 `npm install`）
- **Revit 2026**（資料包內的 `.rvt` 都是 2026 格式，2024 / 2025 打不開），並已安裝、啟動 Revit MCP 外掛（WebSocket，預設 port 8964；可用環境變數 `REVIT_MCP_PORT` 修改）
- 在 Revit 開啟 `revit_models/model_01.rvt` 或 `model_02.rvt`，要建窗的那個模型必須是作用中的專案

不連 Revit 也可以使用步驟 3（氣象分析、產生方案、手動調整、室內風流試算）和指令清單產生器。

完整的操作步驟見 **[執行步驟說明.md](執行步驟說明.md)**。

## 快速開始

1. 在 Revit 開啟 `revit_models/model_01.rvt`，並啟動 MCP 外掛
2. 啟動 API：

   ```bash
   node server.mjs
   ```

3. 開啟 http://localhost:3080，依序操作：
   1. 選模型 01，按「檢查 Revit 連線與模型」
   2. 按「檢查模型內的窗型」，缺少的窗型要在 Revit「插入 > 載入族群」載入
   3. 選氣象檔、月份和可開窗條件，按「分析氣象」看風花圖與逐月風速，再按「產生方案」並點選一個方案
      - 方案卡右上角是評分（滿分 100），「為什麼是這個窗型 / 窗數」可展開選型過程
      - 「室內風流」開大圖看 2D 風流與通風覆蓋率
      - 「手動調整窗數/窗型」可另存成 P1-M1 這類自選方案，不要的可刪除
   4. 輸入方案編號確認 → 檢視會刪除的舊窗 → 「刪除舊窗並建窗」
   5. 在 Revit 檢查 3D 牆體切割和干涉，回到網頁記錄結果，再按「匯出最終檔案」
4. 在 Revit 另存 rvt，放進匯出的資料夾

完成後的樣子可以參考 `examples/`。要套用到新的模型，請看 [docs/workflow_model_A.md](docs/workflow_model_A.md)。

要換建築所在地，就把新的 `.epw` 放進 `data/weather/`，步驟 3 的下拉選單會出現它。下載方式見 `data/weather/how_to_download_epw.md`。

## 範例成果（`examples/`）

| 資料夾 | 內容 |
|---|---|
| `examples/model01_P1/` | 模型 01，P1 北–南 10 扇，VC-006 pass；含建好窗的 `model_01_P1_windows.rvt` |
| `examples/model02_P1/` | 模型 02，P1 北–南 10 扇，VC-006 pass；含建好窗的 `model_02_P1_windows.rvt` |
| `examples/model01_P2-M1/` | 模型 01，**手動方案**範例：以 P2 為底，北、南各 4 扇 1050×1350，東、西各 3 扇 1400×1800，共 14 扇，建窗 14/14 pass；人工碰撞檢查（VC-006）尚未記錄；含 `model_01_P2-M1_windows.rvt` |

三份都是用台南 TMYx 氣象（全年，18–30°C、8–22 時）跑出來的。

只產生 MCP 指令清單（不連 Revit）：

```bash
node cli/build_mcp_commands.mjs --model 01 --proposal P1
```

## 資料夾

| 路徑 | 內容 |
|---|---|
| `server.mjs` | HTTP API 和網頁介面 |
| `web/index.html` | 操作介面 |
| `lib/catalog.mjs` | 模型登錄、檔案讀取 |
| `lib/revit_client.mjs` | Revit MCP WebSocket 用戶端 |
| `lib/proposals.mjs` | 氣象 → 方案（呼叫 `engine_member3/`）、手動方案的新增與刪除 |
| `lib/wind_stats.mjs` | 風花圖、逐月風速、各外牆迎風分數的計算過程 |
| `lib/plan.mjs` | 方案平面示意圖用的幾何（牆、柱、服務核） |
| `lib/airflow.mjs` | 室內風流試算（2D 位勢流）與通風覆蓋率 |
| `lib/mcp_commands.mjs` | 方案 → MCP 建窗指令清單 |
| `lib/executor.mjs` | 依指令清單建窗、回讀、還原、記錄人工檢查 |
| `lib/final_package.mjs` | 匯出最終檔案 |
| `cli/build_mcp_commands.mjs` | 指令清單的命令列版本 |
| `engine_member3/` | 組員 3 的推薦引擎（2026-10-30 加入選型過程紀錄與手動方案函式，自動方案結果不變） |
| `data/models.json` | 模型登錄表（rvt、Revit 專案名稱、資料夾） |
| `data/model_XX/` | `building_data.json`（牆、柱、房間）、`window_families.json`（已載入的窗型）、`proposals_default.json`（預設方案） |
| `data/shared/` | `window_types.json`（窗型參數）、`design_rules_wind.json`（通風規則）、`ventilation_compliance_checklist.json`（規範檢核表） |
| `data/weather/` | EPW 氣象檔；換地點時把新的 `.epw` 放進這裡（下載方式見 `how_to_download_epw.md`） |
| `revit_models/` | 尚未開窗的原始模型 |
| `examples/` | 完成的範例成果，含建好窗的 rvt |
| `output/` | 執行結果（不納入版本控制） |

## API

| 步驟 | 方法 | 路徑 | 內容 |
|---|---|---|---|
| 1 | GET | `/api/models` | 模型清單 |
| 1 | GET | `/api/revit/status` | Revit 連線狀態和作用中專案 |
| 1 | GET | `/api/models/:id/verify` | 比對 Revit 模型和 `building_data.json`（唯讀） |
| 2 | GET | `/api/models/:id/window-types` | 需要的窗型是否都已載入 |
| 3 | GET | `/api/weather` | 可用的 EPW |
| 3 | POST | `/api/weather/summary` | 氣象摘要 `{epw, months?, t_min_c?, t_max_c?, hour_from?, hour_to?, all_hours?}` |
| 3 | POST | `/api/weather/analysis` | 風花圖（16 方位 × 風速分級）與逐月風速表，參數同上 |
| 3 | POST | `/api/models/:id/exposure` | 各外牆迎風分數的計算過程，參數同上 |
| 3 | GET | `/api/models/:id/plan` | 平面示意圖幾何 |
| 3 | GET | `/api/models/:id/proposals?file=` | 讀取方案（預設 `proposals_default.json`），含評分組成與選型過程 |
| 3 | POST | `/api/models/:id/proposals` | 依氣象重新產生方案，參數同上 |
| 3 | GET | `/api/models/:id/manual-options?file=&proposal=` | 手動調整用：各外牆、各窗型最多能放幾扇 |
| 3 | POST | `/api/models/:id/proposals/custom` | 手動方案 `{proposals_file, proposal_id, per_wall: {牆ID: {count, type}}}` → 另存為 P1-M1… |
| 3 | POST | `/api/models/:id/proposals/custom/delete` | 刪除手動方案 `{proposals_file, proposal_id}`（自動方案不能刪） |
| 3 | POST | `/api/models/:id/airflow` | 室內風流試算 `{proposals_file, proposal_id, wind_from_deg?}`，預設用方案檔的主風向 |
| 4 | POST | `/api/commands` | 方案 → 指令清單 `{proposals_file, proposal_id}` |
| 4 | GET | `/api/models/:id/existing-windows` | 模型內既有的窗：前一組方案的窗、非本工具建立的窗 |
| 4 | POST | `/api/build` | 建窗 `{commands_file, confirm, replace_previous?, delete_window_ids?}`，`confirm` 必須等於方案編號 |
| 4 | POST | `/api/review` | 寫入人工檢查結果 `{report_file, collision: pass\|fail, wall_cut, reviewer}` |
| 4 | POST | `/api/rollback` | 刪除這次建立的窗 `{report_file}` |
| 5 | POST | `/api/final` | 匯出最終檔案 `{report_file}` |
| — | GET | `/api/outputs?prefix=` / `/api/file?path=` | 列出或讀取 `output/` 裡的檔案 |

會連 Revit 的請求會排隊，一次只執行一個。

## 切換方案

建立新方案前，會先刪除本工具在同一個模型裡建過、還沒還原的窗（前一組方案），被取代的報告會標上 `superseded_at` 和 `superseded_by`，之後不能再匯出或還原。如果不想刪，傳 `replace_previous: false`。

不是本工具建立的窗（手動畫的，或其他腳本建的）預設保留。如果它們和新方案的位置重疊，會在刪除或建立任何東西之前就停下來，並列出這些窗的 ID；要一併刪除就把 ID 放進 `delete_window_ids`。這樣做是為了避免 Revit 因為窗重疊跳出錯誤對話框、要求手動刪除實體。

如果 Revit 還是因為其他原因跳出對話框並刪掉了剛建的窗，報告會記錄「窗建立後已被 Revit 移除」。

## 每扇窗的建立流程（`mcp_commands` 的 `steps`）

1. `create_window`：`wallId`、`locationX/Y`（mm）、完整的 `族群: 類型`
2. `get_element_info`：回讀「族群與類型」和「主體 ID」，不符就刪除該窗並記為 fail。MCP 遇到名稱對不上時會改用第一個窗型，所以一定要回讀
3. `modify_element_parameter`：窗台高度改成方案值。單位是英尺，900 mm → 2.952755906；MCP 的預設窗台是 914.4 mm
4. `flip_element`（facing）：窗扇朝室外時翻轉成朝室內。判斷方式是範圍框凸出較多的一側應該在牆內
5. `get_element_geometry`：核對沿牆位置（±5 mm）、窗頂高度，並用範圍框檢查有沒有和柱、同一面牆的其他窗重疊

單扇失敗不會重試，會記錄下來繼續做下一扇。完整的 3D 碰撞和牆體切割要在 Revit 人工檢查後，用 `/api/review` 寫回 VC-006。

## 最終檔案（`output/final_modelXX_PX_時間/`）

| 檔案 | 內容 |
|---|---|
| `1_selected_proposal.json` | 選定的方案、氣象、假設 |
| `2_mcp_commands.json` | 實際執行的 MCP 指令清單 |
| `3_build_report.json` | 逐窗回讀結果、ElementId、VC-006 |
| `4_ventilation_checklist.json` | 填好的規範檢核表（VC-001～VC-008） |
| `README.md` | 成果摘要 |

MCP 沒有存檔工具，請在 Revit 另存 rvt 並放進這個資料夾。

## 限制

- MCP 沒辦法載入 `.rfa` 族群，缺少的窗型要在 Revit「插入 > 載入族群」手動載入
- 法定有效通風面積和辦公區面積是 PoC 暫算值，正式使用前須由建築師或門窗廠覆核（VC-008）
- 方案評分只比較通風好壞，不含法規門檻；未達 5% 的手動方案卡片會顯示紅色 ✗
- 室內風流是 2D 位勢流試算：不考慮窗高、家具、渦流與熱浮力，只用主風向，結果只用來比較方案；目前只支援矩形平面
- 窗扇方向是用範圍框判斷的，已在這個窗型（`M_Window-Casement-Double`）的南北東西四個方向實測過；換用其他窗型時請先目視確認
- 兩個模型的 ElementId 不能混用；執行前會辨識 Revit 作用中的是哪個模型，不符就拒絕執行。辨識時先比專案名稱，另存新檔後名稱會變，就改用「只有該模型才有的牆 ID」辨識（`/api/revit/status` 的 `matched_by`）
