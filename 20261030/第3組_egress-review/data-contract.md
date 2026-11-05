# 逃生動線檢討：資料填寫說明 v0.2

依《Revit 逃生動線法規檢討系統｜產品與技術計畫》0.1 修訂。四個人照這份說明交資料，最後才接得起來。
**可以加欄位；不要改名、不要刪欄位。** 要改先在群組講，由一個人改這份檔案並發 PR。

## 五條鐵則（出自計畫書第 1 節）

1. **模型給資料，程式做計算。** 距離、路徑、寬度、轉向角、合不合格，全部由 `engine.js` 這類程式算。AI 只幫忙寫程式，不負責計算或判定。
2. **每個結果都查得到來源。** 寫明是哪個模型、哪個版本的規則、哪個批次算出來的。
3. **法規判定和設計提示分開。** 條文有寫的才叫「符合／不符合」。自訂的保守值一律標「設計提示」。
4. **資料不完整不等於不合格。** 缺資料就標「模型資料不足」或「需人工確認」，並說清楚缺什麼。
5. **工具只是輔助。** 使用類組、例外條款等，最後由專業人員確認。

## 共同規則

- 長度用**公尺**，面積用**平方公尺**。座標 `[x, y]` 用 Revit 專案座標。
- 每個元件都要有 `element_id`（文字）和 `unique_id`。重新擷取時，同一個元件的編號要一樣。
- 不確定的資料填 `null`，**不要猜**。例如門的有效淨寬沒標，就填 `null`。
- 檔案用 UTF-8。

## 接力順序

| 步驟 | 誰 | 交出 | 範例檔 |
|---|---|---|---|
| 1 擷取 | A（Revit 外掛） | 模型快照 | `data/sample-1-模型快照.json` |
| 2 設定 | 使用者在畫面上選 | 專案設定 | `data/sample-2-專案設定.json` |
| 3 檢討 | B（幾何引擎）＋C（法規規則） | 檢討批次 | `data/sample-3-檢討批次.json` |
| 4 呈現 | D（前端） | 畫面 | `egress-review.html` |

規則包：`regs/rules.json`。法規原文：`regs/D0070115_第92條.md`、`regs/D0070115_第93條.md`。

---

## 1 模型快照（A 填，外掛只讀模型、不改模型）

**project**

| 欄位 | 意思 | 例子 |
|---|---|---|
| `model_name` / `model_guid` | 模型名稱與識別碼 | `"專案1"` |
| `revit_version` | Revit 版本 | `"2024"` |
| `phase` / `design_option` | 階段、設計選項 | `"新建構造"` |
| `level` | 樓層名稱與高程 | `{"name":"3F","elevation_m":8.4}` |
| `unit` / `coordinate` | 單位、座標系 | `"m"`、`"Revit 專案座標（Internal Origin）"` |
| `extracted_at` | 擷取時間 | `"2026-10-19T20:00:00+08:00"` |

**rooms**

| 欄位 | 意思 | 不確定時 |
|---|---|---|
| `element_id` / `unique_id` / `category` | 編號、類別 `"Rooms"` | — |
| `number` / `name` / `level` | 房號、名稱、樓層 | — |
| `area_m2` | 面積 | `null` |
| `boundary` | 外框角點，照順序繞一圈 | `[]` |
| `boundary_closed` | 邊界是否封閉 | `false`，並寫進 warnings |
| `door_ids` | 這間房的門 | `[]` |
| `is_habitable` | 是否為居室（§93 只檢討居室） | `null`＝待使用者確認 |

**doors**：`element_id`、`unique_id`、`room_ids`（門兩側的空間）、`position`、`clear_width_m`（有效淨寬，沒標就 `null`）、`clear_width_source`（數值從哪個參數來）。

**stairs**：`element_id`、`unique_id`、`name`、`footprint`、`entry_point`（計算終點）、`endpoint_type`（`"first_step"` 第一階／`"fire_door"` 安全梯防火門，§93 第 2 項）、`is_direct_stair`（是否確認為直通樓梯，未確認填 `null`）。

**corridors**：`element_id`、`unique_id`、`name`、`centerline`、`clear_width_m`、`clear_width_source`（怎麼量的）、`rooms_both_sides`（兩側是否都有居室，§92 用）。

**warnings**：擷取時發現的問題，每筆有 `element_id`、`code`、`message`。例如 `ROOM_NOT_ENCLOSED`、`DOOR_NO_CLEAR_WIDTH`。

## 2 專案設定（使用者手選，程式不可從房間名稱推測）

| 欄位 | 意思 | 例子 |
|---|---|---|
| `use_group` | 使用類組（A-1～I），沒選填 `null` | `"B-3"` |
| `use_group_set_by` | 誰選的 | `"王小明"` |
| `floor_number` | 第幾層（§93 第 15 層以上要折減） | `3` |
| `below_ground` | 是否地下層（§92 面積門檻不同） | `false` |
| `fire_resistant` | 是否防火構造或不燃材料，未確認 `null` | `true` |
| `tv_studio_with_audience` | C 類才要填 | `null` |
| `corridor_serves_classroom` | D-3～D-5 才要填 | `null` |
| `duplex_housing` | 複層式集合住宅（第一階段未實作） | `false` |

## 3 檢討批次（B＋C 的程式產生，前端與報告只讀不改）

**最上層**

| 欄位 | 意思 |
|---|---|
| `batch_id` / `created_at` / `locked` | 批次編號、建立時間、是否鎖定。新批次不覆寫舊批次。 |
| `inputs.snapshot_hash` | 用哪份快照算的 |
| `inputs.rule_pack` / `rule_pack_status` | 規則包版本與核定狀態 |
| `inputs.engine_version` | 引擎版本 |
| `inputs.settings` | 當時的專案設定 |
| `fix_list` | 模型修正清單：哪個元件、缺什麼 |

**rooms[]**

| 欄位 | 誰算 | 意思 |
|---|---|---|
| `status` | C | 只能是 `符合`／`不符合`／`需人工確認`／`模型資料不足` |
| `reasons` | B、C | 為什麼需確認或資料不足 |
| `facts.far_point` / `path` | B | 最遠點、可行路徑（一串點） |
| `facts.indoor_m` / `corridor_m` / `distance_m` | B | 室內段、走廊段、總步行距離 |
| `facts.endpoint_type` / `stair_id` | B | 算到哪座樓梯、算到第一階或防火門 |
| `facts.candidates` | B | 每座候選樓梯的距離 |
| `facts.turns` | B | 每個轉折點的轉向角（0＝直行） |
| `legal.rules` / `limit_m` / `diff_m` / `pass` | C | 套用的規則編號、上限、差多少 |
| `hints` | C | 設計提示（專案保守值，非法定） |
| `manual_review` | 使用者 | 覆核結論、覆核人、說明 |

**corridors[]**：`clear_width_m`、`required_m`、`rule`、`status`、`reasons`。

## 判定順序（C 照這個寫）

1. 房間邊界沒封閉、找不到門、沒有已確認的直通樓梯 → `模型資料不足`。
2. 沒選使用類組、是否居室未確認、防火構造未確認等 → `需人工確認`。距離照算，但不下結論。
3. 以上都沒有 → 距離 ≤ 上限為 `符合`，否則為 `不符合`。
4. 另外檢查專案保守值，觸發的寫進 `hints`，不影響 `status`。

---

範例資料都是假的，由 `node make_samples.js` 產生；`node build.js` 會重新組出前端原型。
