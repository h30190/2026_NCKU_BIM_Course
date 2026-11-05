# MaterialMapping — AI 材料辨識 + LEBR_BlccId 寫入工具

> **2026-10-27 起已整合進控制台**：確認網頁改為 `console/` 的「材料對照」分頁，
> 不再另外啟動（原本的 :8090 後端、:5180 前端已移除）。這個資料夾只留 AI agent 的
> 操作說明（[AGENTS.md](AGENTS.md)）與辨識結果（`data/mapping.json`）。

本工具是「Revit × AI 蘊含碳自動計算助手」提案裡「AI辨識材料命名」那塊的實作，
搭配組員 [LebrCarbon](../LebrCarbon/) 外掛使用，不修改、不重複其計算核心。

## 解決的問題

LebrCarbon 外掛要求使用者**手動**在牆/窗/樓板類型上填 `LEBR_BlccId` 參數，
才能被「從模型帶入」讀取。本工具用 AI（透過 Revit MCP）自動讀取模型裡的牆/窗類型，
比對官方 B-LCC 資料庫（121 筆構件係數），產生 BlccId 對照草稿，
透過網頁讓使用者確認／修改，再寫回 Revit，省掉手動查表填值的步驟。

## 整體架構

```
使用者（在對話裡跟 AI 說「有新的，幫我辨識」）
        │
        ▼
AI Agent（Claude Code + Revit MCP，本機 node server）
        │ 讀：get_wall_types / get_element_info / query_elements
        │ 寫：modify_element_parameter
        ▼
Revit 模型（牆/窗類型的 LEBR_BlccId 共用參數）
        │
        ├──→ 控制台「材料對照」分頁（console/，http://localhost:5173）：顯示辨識結果，下拉選單可改，即時重算小計
        │      API：console/backend/handlers_mapping.go（/api/mapping、/api/blcc）
        │      資料來源：LebrCarbon/src/LebrCarbon.Data/Resources/blcc.LEBR-2025.json（與外掛共用同一份）
        │               MaterialMapping/data/mapping.json（辨識結果存檔，AI agent 直接改這個檔）
        │
        └──→ LebrCarbon 外掛（組員做的，完全沒改）：「從模型帶入」讀取同一個
               LEBR_BlccId 參數 → 官方手冊公式計算 → EEC/CFR/等級 → 組員的網頁控制台
```

**同一個網站、依流程排分頁**：「材料對照」確認材料 → 外掛計算並上傳 →
「評估總覽／快照時間軸／方案比較」看正式 LEBR 結果。

## 完整工作流程（6 步，全部驗證過）

| 步驟 | 內容 | 工具 | 狀態 |
|---|---|---|---|
| ① | 讀取模型牆/窗/門類型 | Revit MCP (`get_wall_types`、`query_elements`...) | ✅ |
| ② | AI 辨識 → BlccId 草稿 | AI 依規則比對 B-LCC（規則見 [AGENTS.md](AGENTS.md)） | ✅ |
| ③ | 讀取模型實際面積 | Revit MCP `query_elements_with_filter` 批次讀取（不要逐一查） | ✅ |
| ④ | 網頁確認／修改 | 控制台「材料對照」分頁，依種類分組、可收合 | ✅ |
| ⑤ | 寫回 `LEBR_BlccId` 參數 | Revit MCP (`modify_element_parameter`，要先在 Revit 按過「綁定參數」) | ✅ |
| ⑥ | 組員外掛「從模型帶入」→「計算」 | LebrCarbon（使用者在 Revit 操作） | ✅ 已用真實模型驗證抓得到、算得出 |

**已經在真實模型（SB_B_250212，不是測試模型）跑過一輪完整流程**：18種牆類型（結構=是的排除、粉刷層併入對應牆體避免重複計算）+ 19種窗類型（材質查無資料，套基準鋁框）+ 6種門（排除）都已辨識並寫回 Revit，組員外掛「從模型帶入」驗證過真的讀得到。

## 範圍（哪些構件可以自動辨識）

**涵蓋**：牆本體（CW，6種）、外牆外裝（OW，6種）、內牆（IW，6種）、窗（W，70種組合）

**排除，且已記錄原因**：
- 主結構（樑柱）：團隊決定不做，列入研究限制
- 樓板（F/P 類別）：本次範圍未涵蓋
- 門：B-LCC 121 筆資料庫完全沒有門的對應類別（cw/ow/iw/f/p/w 六類剛好 121 筆，無第七類），已與使用者確認排除，列入研究限制
- 地下層構件（基礎、擋土牆）：非外殼評估範疇，LebrCarbon 本身也會排除地下層

## 已知限制 / 需要注意

- **B-LCC 牆本體（CW）只有 6 種標準工法**（RC現場澆置 + 5種帷幕系統），**沒有傳統磚造外牆選項**，
  磚牆類的外牆一律近似套用 CW-1（金屬骨架邏輯最接近），這是近似值，非精確對應。
- **B-LCC 完全沒有門的對應類別**，所有門一律排除。
- **窗的實際玻璃/窗框規格大多查不到**（Revit 類型參數裡常是 `N/A` 或 `<依品類>`），
  全部套基準值（鋁框+6mm普通玻璃），demo 前若拿得到窗明細表建議回來修正。
- 不管哪個模型，**凡是標「AI猜測」的項目，用之前一定要在網頁上看過**，尤其是：
  B-LCC沒有精確對應而近似套用的、沒標示粉刷側數的磚牆、「通用-xxxmm」這類範本命名的類型。
- 面積讀取跟材料辨識都是**手動觸發**（跟 AI agent 說「有新的，幫我辨識」才會動），
  不是自動同步；模型改了要主動告知 AI 重新讀取。
- `mapping.json` 用 `project` 欄位區分不同 Revit 檔案的資料，因為**不同檔案可能有相同的 ElementId**
  （同樣來自 Revit 範本的系統族類型）；新增/修改資料時務必帶上 `project`。

## 如何啟動

```powershell
cd ..\console
.\start.ps1
# 瀏覽器開 http://localhost:5173 →「材料對照」分頁，用「Revit 專案」切換不同 Revit 檔案的資料
```

AI agent 改了 `data/mapping.json` 之後不用重啟後端，網頁按「重新整理」或切換分頁就會讀到新內容。
網頁上「材料試算合計」只算新建碳排 × 面積，不是正式 LEBR 評估。

**`localhost` 是「這台電腦自己」的意思，不是共用網址。** 每個人要在自己電腦上跑 `start.ps1`，
才會在自己的 `localhost:5173` 看到網頁；不是一人跑起來、大家都連得到同一個。
資料（`mapping.json`）也是存在各自電腦上，沒有自動同步——誰在哪台電腦上辨識/確認過的結果，
只存在那台電腦，要分享給別人看就是把這個資料夾（含 `mapping.json`）一起傳過去，或是走 git commit。

## 給 AI agent 接手用

怎麼接上 Revit MCP、怎麼判斷 BlccId、怎麼避免重複計算——完整規則寫在 [AGENTS.md](AGENTS.md)，
換電腦、換模型、換人接手都先看那份。

## 跟組員溝通的重點

這個工具不動 `LebrCarbon/` 任何程式碼，只負責把 `LEBR_BlccId` 這個組員外掛本來就有定義的
共用參數自動填好。對組員來說，差別只在於：原本要手動查表填的參數，現在已經被填好了，
其他操作流程（綁定參數、從模型帶入、計算）完全不變。
