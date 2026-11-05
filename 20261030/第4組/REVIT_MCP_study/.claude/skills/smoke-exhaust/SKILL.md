---
name: smoke-exhaust
description: "排煙窗法規檢討：無窗居室判定、無開口樓層判定、排煙有效面積計算、剖面標註、Excel 報告匯出。觸發條件：使用者提到排煙、排煙窗、無窗居室、無開口樓層、建技規§101、消防§188、天花板下80cm、有效開口、煙層。工具：check_smoke_exhaust_windows、check_floor_effective_openings、create_section_view、create_detail_lines、create_filled_region、create_text_note、export_smoke_review_excel。"
metadata:
  references:
    - domain/smoke-exhaust-review.md
    - domain/references/building-code-tw.md
---

# 排煙窗法規檢討

## 工作流程

### 步驟 1：無開口樓層判定
`check_floor_effective_openings` → 檢查外牆有效開口面積是否 ≥ 樓地板面積 1/30（消防§4 + §28③）

### 步驟 2：排煙窗檢討（主要檢查）
呼叫 `check_smoke_exhaust_windows` 時必須明確傳入 `colorize: false`，以取得檢核資料而不進行視圖上色或建立標註視圖。接著依「房間摘要 → 單窗診斷 → 情境試算 → 改善建議」流程整理工具實際回傳結果（詳見下方「單窗診斷與 AI 改善建議」）。

### 步驟 2A：單窗診斷與 AI 改善建議（MVP）

本流程只整理 `check_smoke_exhaust_windows` 的回傳資料，不改變工具的 compliance calculation，也不寫入 Revit。Scenario 數值由 `CheckSmokeExhaustWindows` 的 C# 檢核結果提供。

#### 1. 房間檢核摘要

對每個受檢房間逐項呈現以下 MCP 回傳欄位：

| 顯示欄位 | 回傳欄位 |
|---|---|
| Room ID | `RoomId` |
| Room Name | `RoomName` |
| Room Area | `RoomArea` |
| Required Area | `Summary.RequiredArea` |
| Total Effective Area | `Summary.TotalEffectiveArea` |
| Deficit | `Summary.Deficit` |
| 結果 | `Summary.Result`（PASS / FAIL） |

保持工具回傳的數值與單位，不以 AI 重新計算或覆蓋合規結果。

#### 2. 單窗診斷表

逐窗列出下表欄位，數值直接取自 `Windows[]`，不要補猜缺漏值：

| 顯示欄位 | 回傳欄位 |
|---|---|
| Window ID | `WindowId` |
| Family / Type | `FamilyName` / `TypeName` |
| Width / Height | `Width` / `Height` |
| Sill Height / Head Height | `SillHeight` / `HeadHeight` |
| Height In Zone / Area In Zone | `HeightInZone` / `AreaInZone` |
| Operation Type / Opening Ratio | `OperationType` / `OpeningRatio` |
| Effective Area | `EffectiveArea` |
| Diagnosis | 依下列優先規則產生 |

Diagnosis 規則按順序套用，命中一項後不再套用後續項目：

1. `NeedsManualConfirm == true`：標示「窗型需人工確認」。不可自行判定為 Fixed；列入人工確認事項。
2. 否則，若 `HeightInZone <= 0`：標示「未進入天花板下 800 mm 有效帶」。
3. 否則，若 `HeightInZone > 0` 且 `OpeningRatio == 0`：標示「位於有效帶內，但無法開啟」。
4. 否則，若 `HeightInZone > 0` 且 `OpeningRatio > 0`：標示「可貢獻有效開口」。

如欄位缺漏、無法解析或條件未涵蓋，標示資料不足並要求人工確認，不要自行推斷。

#### 3. 假設改善情境

Scenario 數值由 `CheckSmokeExhaustWindows` 的 C# 檢核結果提供，使用 `Windows[].Scenarios.FiftyPercent` 與 `Windows[].Scenarios.HundredPercent`。直接呈現回傳的 `OpeningRatio`、`EffectiveArea`、`Increase`、`TotalEffectiveArea`、`Deficit`、`IsCompliant` 與 `Result`；AI / Dashboard 不重新計算 Scenario 數值或 PASS / FAIL。

C# 僅對 `AreaInZone > 0` 且 `NeedsManualConfirm == false` 的窗提供 Scenario。稱為「假設改善情境」，不可稱作法規要求、預測的實際改造結果或已核准方案。Scenario 是純計算，不得修改 OpeningRatio 或 Revit 模型。

#### 4. 改善候選排序與房間缺口

分層列出候選，優先次序如下：

1. **有效帶內且已確認的窗**：如有 `Scenarios`，可依回傳的 `Increase` 排序並呈現情境結果。
2. **未進入有效帶的窗**：列出實際回傳的高度與有效帶面積，說明目前不能貢獻有效開口；可建議檢討位置或高度。不可臆測調整尺寸或新增窗尺寸。只有 MCP 回傳了可核實的距離等資料時，才能引用該數值。
3. **已能貢獻有效面積的窗**：如需列出低貢獻窗，按實際 `EffectiveArea` 由小至大排列，並明確稱為檢視候選；沒有資料支持時，不推定它可增加多少面積或應如何改造。

對每個房間，引用 `Summary.Deficit` 說明目前缺口。單窗情境直接引用 C# 回傳的 Scenario 欄位；不得自行加總、重算缺口或用情境結果取代目前 `Summary.Result`。

#### 5. AI 改善建議

建議逐項連結實際 Room / Window ID、回傳面積與 C# 回傳的情境增益。已確認且有 Scenario 的窗可用以下語氣：

> Window {WindowId} 位於有效帶內，目前有效面積為 {EffectiveArea} m²。C# 假設情境結果：50% 有效面積 {Scenarios.FiftyPercent.EffectiveArea} m²（增加 {Scenarios.FiftyPercent.Increase} m²）；100% 有效面積 {Scenarios.HundredPercent.EffectiveArea} m²（增加 {Scenarios.HundredPercent.Increase} m²）。

若 `OperationType` 是 `fixed`，可以稱為固定窗；不得僅因 `OpeningRatio == 0` 就判定為 Fixed。對未進入有效帶的窗可使用：

> Window {WindowId} 未進入天花板下 800 mm 有效帶，目前無法貢獻有效開口。可評估調整窗戶位置或高度，但不得自行假設新的尺寸。

每項房間建議同時說明目前 Deficit 與選定 Scenario 回傳的總有效面積及改善後 Deficit。若 Window 沒有 `Scenarios`，說明該窗不提供此情境試算。

#### 6. 仍需人工確認事項

列出所有 `NeedsManualConfirm == true` 的 Window ID、Family / Type 與工具回傳的 `Note`（若有）。明確要求人工確認窗型及實際可開啟方式；確認前不做 0% 窗戶情境試算，也不把它當作 Fixed 或已確認的改善候選。

#### 安全限制

- 不修改老師既有的 compliance calculation，也不重新判定工具回傳的 PASS / FAIL。
- 不自行修改 OpeningRatio，不將試算比例寫回窗型或 Revit。
- 不自行修改窗戶尺寸，不捏造新窗戶尺寸或工具未回傳的數據。
- 本 MVP 分析流程不直接修改 Revit 模型；分析呼叫必須傳 `colorize: false`，且不得呼叫建立視圖、上色、標註或其他修改模型的工具。既有步驟 3–5 的選用視覺化／匯出工作不屬於本分析流程，只有使用者另行要求時才執行。
- 不將 0 / 0.5 / 1.0 描述成法規直接規定；它們只用作既有窗型設定參照或明確標記的假設改善情境。
- `NeedsManualConfirm == true` 時必須要求人工確認，不可自行判定為 Fixed，亦不可進行情境試算。
- 所有 AI 診斷、排序、試算與建議只能建立在 MCP 本次實際回傳數據上；資料不足時如實說明。

#### 建議輸出格式

1. **房間檢核摘要**：Room ID、Room Name、Room Area、Required Area、Total Effective Area、Deficit、PASS / FAIL。
2. **單窗診斷表**：依上方欄位表列出每扇窗及 Diagnosis。
3. **改善候選排序**：按上述三類優先順序分組；可排序候選列出依據與實際增益，無法量化者標為待評估。
4. **假設情境試算**：直接列出 C# 回傳的 Scenario 50% / 100% 數值與結果，並註明非法律門檻、非模型修改。
5. **AI 改善建議**：按房間 Deficit 與實際候選數據提出建議，直接引用 C# 回傳的情境增益與改善後 Deficit。
6. **仍需人工確認事項**：列出 NeedsManualConfirm 窗戶及待確認資料。

### 步驟 3：剖面檢視（選用）
`create_section_view` → 建立面向指定牆面的剖面視圖，檢視窗戶與天花板高度關係

### 步驟 4：標註（選用）
- `create_detail_lines` → 繪製天花板線、有效帶範圍線
- `create_filled_region` → 填充排煙有效帶色塊
- `create_text_note` → 加入文字標註

### 步驟 5：匯出報告
`export_smoke_review_excel` → 匯出 .xlsx 報告（5 個工作表：樓層總覽、房間明細、窗戶明細、改善建議、§101 補充檢討）

報告自動包含 §101 補充法規檢討：
- **排風量提醒**：排風機 ≥ 120 m³/min（靜態提醒，需人工確認）
- **中央管理室偵測**：自動判斷建築高度 > 30m 或地下面積 > 1000m² 時，搜尋模型中是否有中央管理室

## 法規依據

| 法規 | 內容 |
|------|------|
| 建技規§101① | 排煙口面積 ≥ 防煙區劃面積 2%，設於天花板下 80cm 內 |
| 建技規§101 | 排風機排風量 ≥ 120 m³/min，隨排煙口自動啟動 |
| 建技規§101 | 高度 > 30m 或地下 > 1000m²，排煙控制設於中央管理室 |
| 消防§188③⑦ | 排煙口水平距離 ≤ 30m，每 500m² 以防煙壁區劃 |
| 消防§4 + §28③ | 有效開口 < 1/30 → 無開口樓層，≥ 1000m² 須設排煙設備 |
| 建技規§1 第35款 | > 50m² 居室，天花板下 80cm 通風面積 < 2% → 無窗居室 |

## 參考文件

詳見 `domain/smoke-exhaust-review.md`。
