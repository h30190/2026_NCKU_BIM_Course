# 操作流程：新模型 Model A，窗型用 Revit 內建族群庫

這份流程假設手上有一個新的商辦標準層 `Model A.rvt`，要用這個資料包幫它推薦開窗方案、在 Revit 建窗，最後匯出成果。窗型使用 Revit 內建族群庫。

```
A. 準備模型 → B. 載入窗型 → C. 建立模型資料 → D. 登錄模型 → E. 依氣象產生方案 → F. 確認並建窗 → G. 人工檢查 → H. 匯出與存檔
```

| 階段 | 誰做 | 在哪裡 |
|---|---|---|
| A、B | 建模者 | Revit |
| C | 組員 1（BIM） | Revit MCP，產出 JSON |
| D | 任何人 | 資料包的 `data/` |
| E～H | 使用者 | 網頁 http://localhost:3080 ＋ Revit |

---

## A. 準備模型

1. 另存一份 `Model A.rvt` 的工作檔，原始檔留著當作沒開窗的基底，之後放進 `revit_models/model_03.rvt`。
2. 確認模型符合以下條件：
   - 外牆是一般牆（Basic Wall），位置線是連續的封閉環
   - 標準層在同一個樓層（例如 `L1`）
   - 服務核（樓梯、電梯、廁所）用內牆圍起來；有服務核 Room 更好
   - 專案北向正確（`building_data.json` 的方位以真北為準）
3. 模型裡先不要有窗。原本就有的窗會被當成「非本工具建立的窗」，如果位置和方案重疊，建窗前會要求你決定要不要刪除。

## B. 載入窗型（Revit 內建族群庫）

推薦引擎目前只認得兩個**可開啟**的族群，請從 Revit **公制**族群庫載入：

| 族群 | 檔案 | 引擎中的窗型 |
|---|---|---|
| `M_Window-Casement-Double` | `Windows\M_Window-Casement-Double.rfa` | 側開窗（開啟比例由 `window_types.json` 的開啟角計算） |
| `M_Window-Double-Hung` | `Windows\M_Window-Double-Hung.rfa` | 上下推拉窗（開啟比例暫定 0.5） |

操作方式：Revit「插入」>「載入族群」> 族群庫的 `Windows` 資料夾。族群庫預設位置在 `C:\ProgramData\Autodesk\RVT 20xx\Libraries\` 底下，依 Revit 版本和語言而不同。

注意：

- **族群名稱必須一字不差。** 中文版 Revit 的內建族群庫名稱不同，例如中文樣板裡的窗族群叫「固定」，引擎認不出來。請載入英文公制族群庫的 `M_Window-*` 族群。
- **固定窗不算通風窗。** `M_Window-Fixed` 可以載入，但不會出現在方案裡。
- **要用其他族群**（例如橫拉窗）：需要組員 2 在 `data/shared/window_types.json` 補上窗型資料，組員 3 在 `engine_member3/data.mjs` 的 `buildCatalog()` 加上族群對應，然後重新驗證。
- 窗的尺寸由「類型」決定，MCP 沒辦法任意改寬高。需要的尺寸要先在族群裡有對應的類型，例如 `1400 x 1800mm`。

## C. 建立模型資料（組員 1）

每個模型需要兩個 JSON 檔，放在 `data/model_03/`。目前資料包**沒有自動產生這兩個檔的程式**，請組員 1 用 Revit MCP 讀取模型後整理，格式照 `data/model_01/` 的範例。

### `building_data.json`

| 項目 | 必要欄位 | 用 MCP 取得的方式 |
|---|---|---|
| `models[]` | `model_id: "model_03"`、`name: "Model A.rvt"` | — |
| `walls[]`（每道牆） | `wall_id`、`is_exterior`、`orientation.azimuth_deg`（真北順時針）、`orientation.outward_normal_xyz`、`length_m`、`height_m`、`thickness_m`、`level`、`location_line.start_xyz_m` / `end_xyz_m` / `s_axis_unit_xyz` | `query_elements`（Walls）＋ `get_wall_info` |
| 可排窗區間 | `available_window_intervals[]`：`s_start_m`、`s_end_m`（沿牆距離），扣掉轉角 0.3 m、柱每側 0.1 m | 柱子用 `get_element_geometry` 的範圍框投影到牆上再扣掉 |
| `columns[]` | `ElementId`、`TypeName`、`BoundingBox`（mm） | `query_elements`（Structural Columns）＋ `get_element_geometry` |
| `rooms[]` | `room_id`、`name`、`area_m2`、`level`（服務核 Room；有辦公 Room 也可以放） | `query_elements`（Rooms）＋ `get_element_info` |

### `window_families.json`

步驟 B 載入的每個族群和類型：

```json
{
  "family_name": "M_Window-Casement-Double",
  "supports_ventilation_by_family_name": true,
  "types": [
    { "type_id": "123456", "type_name": "1400 x 1800mm", "mcp_windowType": "M_Window-Casement-Double: 1400 x 1800mm",
      "nominal_width_mm": 1400, "nominal_height_mm": 1800 }
  ]
}
```

類型清單用 `list_family_symbols`（`filter: "M_Window"`）查詢。這個工具最多只回傳 100 筆，所以一定要加 filter。

### 檢查

- 外牆要圍成封閉環，否則辦公區面積會算錯
- `wall_id` 都是 Model A 自己的 ElementId，不能從其他模型複製

## D. 登錄模型

在 `data/models.json` 的 `models` 加一筆：

```json
{
  "model_id": "03",
  "name": "Model A",
  "rvt_file": "revit_models/model_03.rvt",
  "revit_project_name": "Model A",
  "data_dir": "data/model_03",
  "level": "L1",
  "description": "（簡短描述，例如服務核位置）"
}
```

- `model_id` 要用**數字**（API 路徑只接受數字），已經有 01、02，所以用 `03`。
- `revit_project_name` 填 Revit 開啟後的專案名稱，通常就是檔名。之後另存新檔讓名稱改變也沒關係，API 會改用「只有這個模型才有的牆 ID」辨識。
- 重新啟動 API（`node server.mjs`）才會讀到新模型。

## E. 依氣象產生方案

1. 換地點的話，把該地的 EPW 放進 `data/weather/`（下載方式見 `data/weather/how_to_download_epw.md`）。
2. 在 Revit 開啟 Model A，啟動 MCP 外掛。
3. 打開 http://localhost:3080：
   1. **步驟 1**：選 `03 — Model A`，按「檢查 Revit 連線與模型」，應該顯示牆、柱、房間都一致
   2. **步驟 2**：按「檢查模型內的窗型」，應該顯示窗型都已載入；缺少的話回到 B
   3. **步驟 3**：選 EPW、月份（空白代表全年）、可開窗氣溫和時段，按「產生方案」

Model A 第一次使用時還沒有 `proposals_default.json`，步驟 3 一開始會顯示找不到檔案，這是正常的，按「產生方案」就會產生。想讓之後一打開就有預設方案，可以把產生的 `output/proposals_model03_*.json` 複製成 `data/model_03/proposals_default.json`。

方案卡片會列出評分、各牆的窗數、平面示意圖和推薦原因（VC-001～VC-008 檢核結果保留在方案檔與步驟 5 的匯出檔，卡片上不顯示）。可以用「室內風流」比較方案、用「手動調整窗數/窗型」微調，再點選要用的方案。各功能的用法見根目錄的 `執行步驟說明.md`。

注意：室內風流試算目前只支援外牆是正南北、東西向的矩形平面。Model A 如果不是矩形，這個按鈕會顯示錯誤，其他功能不受影響。

## F. 確認並建窗

1. **步驟 4**：輸入方案編號（例如 `P1`），按「確認方案」。
2. 畫面會列出模型裡既有的窗：
   - **前一組方案的窗**（這個 API 建的）：會自動刪除
   - **非本工具建立的窗**：預設保留，勾選的才會刪除；和新方案重疊的窗一定要刪，否則程式會停下來並幫你勾好
3. 按「刪除舊窗並建窗」。每扇窗會依序：建立 → 核對族群與類型、宿主牆 → 窗台改成 900 mm → 窗扇轉向室內 → 核對位置和高度 → 檢查有沒有和柱、同一面牆的其他窗重疊。
4. 結果表格每扇都應該是 `pass`、窗扇是 `interior`。

如果 Revit 跳出錯誤對話框（例如窗切不開牆），被刪除的窗會在報告裡記為「窗建立後已被 Revit 移除」。把方案編號、第幾扇、牆 ID 回報給組員 3 調整位置。

要換方案就回到步驟 3 點另一個方案，再做一次 F。前一組方案的窗會自動刪除。

## G. 人工檢查（Revit）

1. 在 3D 視圖確認每扇窗都正確切開牆
2. 確認窗扇朝室內。這是用範圍框判斷的，換了新的族群時一定要目視確認一次
3. 執行干涉檢查：「協同作業」>「干涉檢查」，選窗對結構柱和牆
4. 回到網頁步驟 4，選擇檢查結果、填入檢查人，按「寫入 VC-006」

## H. 匯出與存檔

1. **步驟 5**：按「匯出最終檔案」，產生 `output/final_model03_P1_時間/`：
   - `1_selected_proposal.json`：方案、氣象、假設
   - `2_mcp_commands.json`：實際執行的指令
   - `3_build_report.json`：逐窗結果、窗 ID、VC-006
   - `4_ventilation_checklist.json`：填好的規範檢核表
   - `README.md`：成果摘要
2. **在 Revit 另存 rvt**（MCP 沒有存檔工具），例如 `model_03_P1_windows.rvt`，放進上面的資料夾。不要覆蓋 `revit_models/model_03.rvt`。
3. Revit 另存時會產生 `.0001.rvt` 這類備份檔，公開前請刪掉。

---

## 常見問題

| 狀況 | 原因／處理 |
|---|---|
| 步驟 1 顯示「無法辨識為任何模型」 | Revit 作用中的不是 Model A，或 `revit_project_name` 填錯；在 Revit 切換到 Model A 的視窗 |
| 步驟 3 方案是空的或很少 | 可排窗區間太短、窗型太大，或族群名稱不符引擎對應（見 B） |
| 建窗報告出現「類型或宿主牆不符」 | 族群或類型名稱沒有完全對上，MCP 改用了第一個窗型；程式已經刪掉這扇窗，請檢查 `window_families.json` 的 `mcp_windowType` |
| 建窗前提示「會和新方案重疊」 | 模型裡有非本工具建立的窗擋住新位置；勾選後再建 |
| 窗台不是 900 mm | 參數名稱在中文版是「窗台高度」、英文版是「Sill Height」；英文版請把 `data/models.json` 的 `sill_parameter_names` 順序調成英文在前 |
| 需要復原 | 步驟 4 的「還原這次建窗」會刪除這次建的窗；已被新方案取代的紀錄不能還原 |

## 目前的限制

- `building_data.json` 和 `window_families.json` 要手動整理（階段 C）
- 引擎只支援 `M_Window-Casement-Double` 和 `M_Window-Double-Hung` 兩個可開啟族群
- MCP 無法載入族群、無法存檔，這兩件事要在 Revit 手動做
- 法定有效通風面積和辦公區面積是 PoC 暫算值，正式使用前須由建築師或門窗廠覆核（VC-008）
