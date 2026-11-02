# AGENTS.md — MaterialMapping

給接手這個工具的 AI agent（Claude Code 或其他）看的操作說明。人類使用者請看 [README.md](README.md)。

## 這個工具是什麼

AI 透過 Revit MCP 讀取 Revit 模型的牆/窗類型，比對官方 B-LCC 碳排資料庫（與外掛共用：`../LebrCarbon/src/LebrCarbon.Data/Resources/blcc.LEBR-2025.json`，121 筆），產生「這個類型該對應哪個 BlccId」的草稿，經人工在網頁上確認/修改後，寫回模型的 `LEBR_BlccId` 共用參數，供 [LebrCarbon 外掛](../LebrCarbon/)（組員寫的，不要動）讀取計算正式碳排。

## 前置：怎麼接上 Revit MCP

1. Clone `shuotao/REVIT_MCP_study`（課程 10/2 教材指定的 repo），`cd MCP-Server && npm install && npm run build`
2. `cd MCP && dotnet build -c Release.R{年份後兩碼}`（例：Revit 2027 用 `Release.R27`；若該版號未在 csproj 的 `<Configurations>` 列出，先手動加進去，Nice3point SDK 會自動依後兩碼解析版本，不用額外處理）
3. 把編譯出的 DLL + `.addin` 複製進 `%AppData%\Autodesk\Revit\Addins\{完整年份}\`（參考 `scripts/install-addon.ps1` 的複製邏輯；若該腳本的版本白名單沒收錄你的 Revit 版本，直接手動複製 `MCP\bin\Release.R{後兩碼}\*` 全部檔案 + `.addin`，不用等腳本更新）
4. 在你執行 Claude Code 的這個專案根目錄（session 一開始的工作目錄）放一份 `.mcp.json`：
   ```json
   { "mcpServers": { "revit-mcp": { "type": "stdio", "command": "node",
     "args": ["<絕對路徑>/REVIT_MCP_study/MCP-Server/build/index.js"], "env": {} } } }
   ```
5. 開 Revit → 開啟要處理的專案 → ribbon「增益集」頁籤 →「MCP Tools」面板 → 按「MCP 服務（開/關）」
6. **重開整個 Claude Desktop app（不是只關分頁）**，或是 Fork 現有對話 session，MCP 連線才會生效
7. 用 `mcp__revit-mcp__get_project_info` 驗證連線，應該回傳目前 Revit 開啟的專案名稱

## 另一套 MCP：rvt-mcp（2026-10-27 用 Revit 2026.5 + 專案1 實測通過）

上面是課程教材 `shuotao/REVIT_MCP_study` 的接法。若你的 Claude Code 連的是 **rvt-mcp**（工具名稱是 `mcp__rvt-mcp__revit_*`），
流程一樣，只是工具不同。實測結果：讀到的面積與外掛「從模型帶入」完全一致（外牆 436.08、隔間 82.43、窗 25.92 m²），
寫入 `LEBR_BlccId` 後外掛讀得到，算出的 CFR 與控制台示範方案一致。

| 步驟 | REVIT_MCP_study | rvt-mcp |
|---|---|---|
| 連線 | `get_project_info` | `revit_list_available_targets` → `revit_switch_target`（`version` 填年份，例如 `"2026"`） |
| 確認參數已綁定 | — | `revit_list_project_parameter_bindings`（`nameFilter: "LEBR"`），`LEBR_BlccId` 應綁在牆、窗、門、樓板、帷幕板的**類型** |
| 列出構件 | `get_wall_types`、`query_elements` | `revit_ai_element_filter`（`category: "牆"` / `"窗"` / `"門"`），回傳每個元件的 `elementId` 與 `typeName` |
| 讀類型參數（功能、現有代碼） | `get_element_info` | `revit_get_type_parameters`（`elementIds` 給每種類型各一個實例即可），回傳 `typeId`、`功能`、`LEBR_BlccId` |
| 讀面積、結構 | `query_elements_with_filter` | `revit_get_element_parameters`：取 `面積`（`HOST_AREA_COMPUTED`）與 `結構`（`WALL_STRUCTURAL_SIGNIFICANT`） |
| 寫入代碼 | `modify_element_parameter` | `revit_set_type_parameter_values`（`typeIds`、`parameterName: "LEBR_BlccId"`、`value: "CW-0;OW-1"`），回應有 `oldValue` / `newValue` |

rvt-mcp 實測踩到的坑：

- **品類要用中文名稱**：中文版 Revit 傳 `"Walls"` 會回「Category 'Walls' not found」，要傳 `"牆"`、`"窗"`、`"門"`。
- **面積不要用 `revit_compute_element_area`**：它算的是元件「所有面」的總面積（兩側加端面），不是 LEBR 用的單面面積。
  用 `revit_get_element_parameters` 的 `面積`；`rawValue` 是平方英尺，乘 0.09290304 換成 m²（`displayValue` 已是 m²，但有四捨五入）。
- **「結構」要看實例參數**：類型參數裡的 `結構` 是空的，要用 `revit_get_element_parameters` 讀實例的 `結構`（是／否）。
- **一次讀太多元件會超出輸出上限**：每個元件約 40 個參數，十幾道牆就會被存成檔案。依類型分批讀，或把存下的檔案用程式解析加總，不要逐一手抄。
- **寫入後用 `revit_get_type_parameters` 讀回確認**，不要只看寫入時的回應。
- **外掛按「計算」會自動存檔**到模型旁的 `{模型名}.lebr.json`。測試時若暫時改了代碼，記得改回後再「從模型帶入 → 計算」一次，評估檔才會復原。
- 窗的 `玻璃_材料`、`外框_材料` 常是 `<依品類>`，讀不出規格，只能看類型名稱判斷（與下方「判斷規則」第 5 點相同）。

## 常見卡關（今天實際踩過的坑）

接 Revit MCP 這段，步驟寫出來不等於照做就會一次成功，下面是今天實際發生過、會重複出現的狀況：

| 症狀 / 錯誤訊息 | 原因 | 解法 |
|---|---|---|
| `dotnet build -c Release.R{XX}` 說該 configuration 不存在 | csproj 的 `<Configurations>` 清單沒列出這個 Revit 版本（例如一開始只有 R24/R26，沒有 R27） | 手動把 `Release.R{XX}` 加進 `MCP\RevitMCP.csproj` 的 `<Configurations>`（`20261016/sample` 不在第 6 組交件範圍，在課程 repo 裡不要提交對它的修改）；Nice3point SDK 會自動依版號後兩碼找對應的 RevitAPI NuGet 套件，不用額外設定 |
| `install-addon.ps1` 報錯說版本不支援（`[ValidateSet(...)]`） | 腳本的版本白名單沒收錄這個 Revit 年份 | 不要等腳本更新，直接手動把 `MCP\bin\Release.R{XX}\*` 全部檔案 + `.addin` 複製到 `%AppData%\Autodesk\Revit\Addins\{完整年份}\`（對應資料夾放 DLL，上一層放 `.addin`） |
| Revit MCP 工具（`mcp__revit-mcp__*`）完全不在這個 session 的工具清單裡 | `.mcp.json` 是在 session **啟動時**讀取的，中途改檔案或只關分頁不會重新讀 | 改完 `.mcp.json` 後，**整個 Claude Desktop app 關掉重開**，或是 Fork 現有對話成新 session（Fork 會帶著對話記錄、但走全新進程，會重新讀 `.mcp.json`） |
| 呼叫 Revit MCP 工具回傳空的 `Error:`、或逾時 | Revit 可能正卡在某個操作中間（彈窗沒關、指令還沒結束），或是短時間內連續發太多次呼叫 | 先確認 Revit 視窗沒有卡住的對話框、沒有還在進行中的指令；多次呼叫改成一次一個、不要同時並行發送（並行很容易逾時） |
| `modify_element_parameter` 回傳「找不到參數: LEBR_BlccId」 | 組員外掛的 `LEBR_BlccId` 等共用參數是**每個 Revit 文件各自要綁定一次**，換了新模型就要重按 | 請使用者在 Revit 裡：「低碳建築」頁籤 →「綁定參數」，按一次（每個 `.rvt` 檔都要各按一次，不是裝了外掛就自動有） |
| 寫入 Revit 後，`get_element_info` 讀回來看不到剛寫的參數值 | 可能是查詢時機問題（文件還沒 regen），不代表寫入失敗 | 用 `modify_element_parameter` 的回傳訊息（有沒有「成功修改參數」）判斷是否成功，不要單靠 `get_element_info` 讀不到就認定失敗；必要時重新查一次 |

## 操作流程（處理一個新模型）

```
1. get_project_info 確認連到哪個檔案
2. get_wall_types 列出所有牆類型；query_elements(category=Windows/Doors) 列窗門
3. query_elements_with_filter(category=Walls, returnFields=["結構","面積","功能"...], maxCount=1000)
   一次批次讀取，不要逐一查詢369道牆這種事（會被Revit MCP連線擋，常常逾時）
4. 依下面「判斷規則」給每個類型一個 BlccId 草稿
5. 寫進 data/mapping.json（記得加 project 欄位！見下方「多專案」）
6. 請使用者開控制台（`../console/start.ps1`，http://localhost:5173）的「材料對照」分頁看過、改過；
   後端會在檔案修改後自動重讀，不用重啟
7. 使用者確認後，用 modify_element_parameter 把 bodyId;finishId 寫進該類型的 LEBR_BlccId
   - 寫入前必須確認該 Revit 文件已經按過組員外掛的「綁定參數」，否則會報錯「找不到參數」
   - 寫入目標是「類型」的 ElementId，不是牆的實例 ID；窗/門也一樣，要先查「類型 ID」欄位
     （get_element_info 讀一個實例，Parameters 裡會有「類型 ID」這個欄位）
```

## 判斷規則（怎麼決定 BlccId）

### 第一步：決定要不要排除

- **查「結構」參數**（`get_element_info` 或 `query_elements_with_filter` returnFields 加 `"結構"`）：`是` → 排除，屬於主結構，不在這個工具的範圍（團隊決定主結構另案處理）
- **樓層在 GL（或明顯是基礎/地下）且厚度異常大（40cm+）** → 通常是基礎/擋土牆，排除
- **門（Doors 類別）** → B-LCC 121 筆資料庫完全沒有門的對應類別（cw/ow/iw/f/p/w 六類加總剛好121筆），**一律排除**，不用每次重新確認
- **型錄裡有、但模型裡 0 個實例的類型** → 不用處理，跳過
- **獨立建模的面材層**（例如「粉刷」「粉刷外」跟旁邊的牆位置重疊，是同一道牆分開畫的）→ **這點必須問使用者確認**，不能自己假設；確認是同一道牆的話，把它的碳排併進對應牆體的 BlccId，這個面材層本身排除（避免重複計算）

### 第二步：分類（牆本體 CW／外裝 OW／內牆 IW／窗 W）

讀 `../LebrCarbon/src/LebrCarbon.Data/Resources/blcc.LEBR-2025.json`，`category` 欄位：
- `cw`：牆本體（不透光帷幕及一般外牆），只有 6 種標準工法（RC現澆、金屬板/玻璃/石材/陶瓷鋼板帷幕、預鑄PC），**沒有傳統磚造選項**
- `ow`：外牆外裝材（磁磚/乾式石材/塗料/金屬模板RC），6種
- `iw`：內隔間，6種（磚牆雙面粉刷、輕質灌漿、矽酸鈣板輕隔間、RC隔間、清水空心磚、水泥粉刷空心磚）
- `w`：外窗，70種（含一般外窗 W-{玻璃1~15}-{AL/PL/WD} 跟帷幕窗 CWW-{玻璃}-{AL/SS}，混在同一個category裡）

判斷依據（依優先順序）：
1. **類型名稱直接對到 B-LCC 的 name**（例如「矽酸鈣板_12cm」對到「輕隔間牆（矽酸鈣板）」）→ 高信心，`confidence: "confirmed"`
2. **類型的「功能」參數**（室外/室內）決定走 CW+OW 還是 IW
3. **名稱裡的側數線索**（雙側/單側/浴室）：雙側粉刷通常對基準款（IW-1）；浴室牆常用水泥砂漿（IW-6較合理）；單側粉刷 B-LCC 沒有對應選項，近似套用但會偏高估，要備註
4. **B-LCC 選項涵蓋不到的工法**（例如磚牆外牆、金屬立柱牆）→ 近似套用結構邏輯最像的選項（例如 CW-1 金屬骨架系統），**務必寫清楚這是近似值**，`confidence: "guess"`
5. **窗的材質查不到**（`get_category_fields(Windows)` 的 TypeFields 有「玻璃材質」「裝飾外框材料」，但常常是 `N/A` 或 `<依品類>`）→ 全部套基準 `W-1-AL`（崁入式鋁框+6mm普通玻璃），`confidence: "guess"`，備註寫明實際規格未知

### 信心標記的意義

- `confirmed`：名稱直接對應、或使用者在對話裡明確確認過
- `guess`：AI 推斷，可能是近似值或範圍猜測，**一定要讓使用者在網頁上看過再寫入 Revit**

## 減碳建議（AI 判斷，不要寫成固定演算法）

這不是材料辨識，是辨識完、寫入 Revit、算出碳排**之後**的下一步。使用者會用自然語言講目標/限制，
每次可能不一樣，例如：「減碳10%」、「內牆不要動，但我要減碳10%」、「把某構件換成指定材料」。

**這一步要在對話裡做，不要做成網頁功能、不要寫成固定演算法（例如「永遠取碳排最低的選項」）**，理由：

- 條件是任意自然語言組合，做成 UI 表單只會太死板，或等於在網頁裡重做一個簡化版的 AI 對話框，純粹多工
- 這本來就是最初提案裡「判斷交給 AI」的部分，不是「計算交給程式」的部分——程式只負責算術，選哪些候選組合要 AI 自己判斷
- 使用者明確否決過把這段搬到網頁上用非同步方式處理過（送出需求、等 AI 回來處理），理由是太怪、不即時

### 流程

```
1. 讀 data/mapping.json 這個 project 目前所有項目（kind != "excluded" 的才算範圍）
2. 讀 B-LCC（../LebrCarbon/src/LebrCarbon.Data/Resources/blcc.LEBR-2025.json），
   取同一 category 裡的其他候選（cw換cw、ow換ow、iw換iw、w換w，不跨類別）
3. 依使用者的目標/限制判斷要動哪些構件、換成什麼——這段是 AI 自己想組合，
   不是程式自動排序取最低值；精確的省多少 kgCO2e 可以用程式/計算機算，但「選哪些候選來組合」要自己判斷
4. 產出多組建議（使用者沒指定數量時，預設 5 組），每組都要講清楚：
   - 改了哪些構件、從什麼 BlccId 換成什麼 BlccId
   - 預估省多少 kgCO2e、佔目前這個 project 總碳排的百分比
   - 一句話說明取捨（例如「只動局部材質不改外牆系統，施工面影響小」vs「換整個外牆工法，效果大但牽涉結構介面」）
5. 使用者選一組，或要求調整、重新給
6. 使用者確認後才動手寫入：
   a. 更新 mapping.json 對應項目的 bodyId/finishId，note 寫明這是哪組建議、原本是什麼、為什麼
   b. modify_element_parameter 寫回 Revit 的 LEBR_BlccId
   c. 請使用者去 LebrCarbon 外掛「從模型帶入 → 計算 → 上傳到控制台」，產生新快照
   d. 請使用者到控制台「方案比較」分頁，拿新快照跟原本的比較，取得正式官方結果（不要自己宣稱算出來的數字就是正式結果，正式結果一定要走外掛）
```

### 重要計算規則：帷幕系統（CW-1~CW-5）不要再疊加 OW

`cw` 類別裡，`CW-0`（傳統 RC 現澆）是「裸牆」，要另外配一個 `ow` 代碼當外裝材（例如 `CW-0;OW-1`）。
但 `CW-1`～`CW-5`（金屬板/玻璃/石材/陶瓷鋼板帷幕、預鑄 PC）本身已經是**含外裝材的完整帷幕系統**。
**建議換成這幾個的時候，要把 `finishId` 設成 `null`（拿掉 OW），不要繼續配外裝代碼**——
疊加會重複計算外裝的碳排，換算下來會明顯低估「換成帷幕系統」能省下的量（少算了被拿掉的那層外裝）。

### 範圍限制（跟材料辨識一樣）

- 只能動 `kind` 是 `wall`／`glazing`／`interior`／`window` 的項目，`excluded` 的（結構牆、門、面材層本身）不要碰
- B-LCC 資料**沒有防火時效、隔熱 U 值這些欄位**，建議只能依碳排數字排序，沒辦法篩選「符合防火限制」，
  要在建議文字裡誠實講清楚這個限制，不要假裝有檢查過

## 多專案：project 欄位

`mapping.json` 的每個項目都要有 `project` 欄位（填 `get_project_info` 回傳的 `ProjectName`）。**不同 Revit 檔案的 ElementId 可能剛好相同**（尤其是同一份範本帶出來的系統族類型，例如帷幕牆、預設磚牆類型），寫入/更新時一定要連 `project` 一起比對，控制台後端的 PATCH 已經這樣做了（看 `../console/backend/handlers_mapping.go` 的 `handleMappingUpdate`），新增程式碼時不要漏掉這個比對。

## 不要做的事

- 不要改 `../LebrCarbon/` 裡任何程式碼，那是組員的計算核心
- 不要在沒有使用者確認的情況下把 `guess` 等級的項目寫進 Revit（高信心的 `confirmed` 可以直接寫）
- 不要省略「結構」參數檢查就假設牆是不是主結構——厚度不是可靠依據（同一個模型裡 RC_15cm 可能是結構=否的外殼牆，RC_60cm 可能是結構=是的基礎牆）
- Revit MCP 單一時間只能一個連線，多個 agent/client 不要同時連
