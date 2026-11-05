# LEBR 資料庫

Revit 碳排外掛使用的《2025 低碳（低蘊含碳）建築評估手冊》資料庫。外掛只讀 `dist/lebr_2025.json`。

> **目前狀態：草稿（`review_status: draft`）**，尚未人工核對完成。核對完成前，計算結果只能當參考。

## 資料夾內容

| 路徑 | 內容 | 誰會改 |
|---|---|---|
| `dist/lebr_2025.json` | **外掛讀這個檔案**（程式產生，不要手改） | 程式 |
| `draft/p_lcc_2022.csv` | 附錄一 初級資材（120 筆），從 PDF 抽出 | 程式 |
| `draft/b_lcc_2025.csv` | 附錄二 建築構件（187 筆），從 PDF 抽出 | 程式 |
| `draft/LEBR資料庫_核對用.xlsx` | 中文核對表，人工核對時用 | 你 |
| `corrections.csv` | **修正清單**：核對後要改的值寫在這裡 | 你 |
| `source/parameters_2025.json` | 第三～六章參數表與公式常數（表 3-1、4-1～4-7、5-1、5-2、6-1） | 手動維護 |
| `scripts/` | 抽表、產生核對表、產生 JSON 的程式 | — |
| `csharp/LebrDatabase.cs` | 外掛讀取 JSON 的 C# 程式 | — |
| `csharp/LebrDatabase.Tests/` | C# 讀取程式的測試 | — |

## 資料流程

```
手冊 PDF ──extract_lebr_tables.py──▶ draft/*.csv ──build_review_xlsx.py──▶ 核對用.xlsx（人工核對）
                                         │                                        │
                                         │                         發現錯誤 → 寫進 corrections.csv
                                         ▼                                        │
         source/parameters_2025.json ──▶ build_json.py ◀─────────────────────────┘
                                         │
                                         ▼
                                dist/lebr_2025.json ──▶ Revit 外掛（LebrDatabase.cs）
```

重點：**不要直接改 CSV 或 JSON**。它們都是程式產生的，重跑就會被蓋掉。要改的值一律寫進 `corrections.csv`。

## 指令

在 `lebr-db` 資料夾執行：

```bash
python -I scripts/build_json.py .
```

只有手冊改版、需要重新從 PDF 抽表時，才跑下面兩個指令。注意：第二個指令會產生新的核對用 Excel，蓋掉舊檔，請先備份已經填寫的檔案。

```bash
python -I scripts/extract_lebr_tables.py "<手冊PDF路徑>" draft
```

```bash
python -I scripts/build_review_xlsx.py "<手冊PDF路徑>" draft
```

執行 C# 測試（確認 JSON 能被外掛正確讀取）：

```bash
dotnet run --project csharp/LebrDatabase.Tests -f net8.0
```

## 修正清單 `corrections.csv` 怎麼填

每一列是一筆修正：

| 欄位 | 說明 | 範例 |
|---|---|---|
| 資料庫 | `P-LCC` 或 `B-LCC` | `B-LCC` |
| 編號 | P-LCC 用 `PLCC-001`；B-LCC 用構件編號 | `CWW-G04-ALPVC` |
| 欄位 | 要改的欄位（見下方） | `F_new` |
| 原值 | 目前資料裡的值（程式會比對，不一致就停止） | `55.92` |
| 修正值 | 改成多少 | `54.92` |
| 依據 | 為什麼這樣改 | `手冊 p.58，a+b = 24.6+30.32` |
| 決定者 | 誰決定的 | `王小明` |
| 日期 | | `2026-10-25` |
| 說明 | 其他備註 | |

可以修正的欄位：
- P-LCC：`a1` `a2` `a3` `a4` `total_a1_a4` `name` `unit` `category` `merged_span` `note`
- B-LCC：`base_a` `surface_b` `F_new` `renew_base_c` `renew_surface_c` `F_renew` `delta_F` `name` `is_baseline` `baseline_code`

用 Excel 編輯後，存檔時請選 **「CSV UTF-8（逗號分隔）」**，否則中文會亂碼，程式也會拒絕讀取。

改完後重跑 `build_json.py`。每一筆修正都會記錄在 JSON 該列的 `corrections` 和最外層的 `corrections_applied`，之後可以追溯。

全部核對完成後，把 `scripts/build_json.py` 裡的 `REVIEW_STATUS` 改成 `"reviewed"`，並在下方「核對紀錄」寫上核對人與日期。

## JSON 結構

```jsonc
{
  "meta":       { "manual_version": "2025", "schema_version": "1.0", "review_status": "draft", ... },
  "parameters": { "life_cycle_renewal": {...}, "structure_factor_W": {...}, "shape_factor_f1": {...},
                  "aspect_factor_f2": {...}, "cantilever_factor_f3": {...}, "baseline_structure_scenario": {...},
                  "waste_density_Wd": {...}, "grade_scale": {...}, "life_extension_LL": {...},
                  "formula_constants": {...}, "component_groups": {...}, "wear_classes": {...} },
  "p_lcc": [ { "id": "PLCC-001", "name": "鋼胚（高爐）", "unit": "kg", "a1": 2.26, ..., "total": 2.27,
               "source": { "book_page": 52 }, "check": { "status": "OK" } } ],
  "b_lcc": [ { "code": "W-G01-AL", "group": "W", "wear_class": null, "name": "...",
               "f_new": 37.14, "f_renew": 0, "delta_f": 0, "is_baseline": true, "baseline_code": "W-G01-AL", ... } ],
  "corrections_applied": []
}
```

- `check.status`：`OK` 驗算通過；`CHECK` 手冊本身數字不一致（已對照原圖確認不是抽取錯誤）；`INFO` 只有總量、無法驗算。
- `wear_class`：`H` 高耗損、`M` 中耗損、`L` 低耗損、`S` 46-6 分戶樓板；不分類別的附表為 `null`。
- 範圍查表一律是「下限不含、上限含」（例如 1.2 < PAr ≦ 1.4），與手冊表格一致。

### 構件編號規則（草案，團隊可改）

| 附表 | 編號格式 | 範例 |
|---|---|---|
| 2-1 外牆外裝 | `OW-序號` | `OW-01` |
| 2-2 外窗 | `W-G玻璃序號-窗框` | `W-G01-AL`（鋁框）、`-PVC`（塑鋼）、`-WD`（木） |
| 2-3 透光帷幕 | `CWW-G玻璃序號-窗框` | `CWW-G01-ALPVC`、`-SS`（不銹鋼） |
| 2-4 不透光帷幕 | `CW-序號` | `CW-00`（基準 RC 外牆） |
| 2-5 內隔間 | `IW-類別-序號` | `IW-H-01` |
| 2-6 室內地坪 | `F-類別-G/W序號` | `F-L-G01`（低耗損一般地坪）、`F-M-W01`（中耗損木作地坪） |
| 2-7 戶外地坪 | `P-類別-RC/GR序號` | `P-H-RC01`（RC 基礎）、`P-L-GR01`（碎石基礎） |

## 外掛怎麼用

1. 把 `csharp/LebrDatabase.cs` 放進外掛專案（例如 `MCP/Core/Lebr/`），把 `dist/lebr_2025.json` 和外掛 DLL 放在一起。
2. 外掛啟動時讀一次就好，存起來重複使用：

```csharp
var db = LebrDatabase.Load(Path.Combine(addinFolder, "lebr_2025.json"));

// 依 Revit 類型上的 LEBR_Code 查構件
var wall = db.GetComponent("IW-M-03");                 // 輕隔間牆（矽酸鈣板），中耗損
var perM2 = wall.FNew + wall.FRenewFor(1.0);           // 新建 + 更新，kgCO2e/m2
var baseline = db.GetBaseline(wall);                   // 基準案：同面積改用磚牆雙面粉刷

// 參數查表
db.ShapeFactorF1(perimeterM, areaM2);                  // 表4-3
db.GradeFor(0.18);                                     // 表5-1 → "1"
db.Parameters.Constant("structure_C", "base");         // 式4 常數 224
```

C# 程式維持 C# 7.3 寫法，Revit 2022～2024（.NET Framework 4.8）與 2025 以後（.NET 8）都能編譯，已用測試專案在兩種環境驗證。

## 計算引擎與 Revit 外掛（組員 2 的模組，初版）

| 路徑 | 內容 |
|---|---|
| `csharp/LebrEngine.cs` | 計算引擎：讀數量格式、依式 1～27 算設計案與基準案，不使用 Revit API |
| `samples/appendix3_quantities.json` | 手冊附錄三範例的數量（數量格式範本，組員 1 的輸出應與它相同） |
| `revit-addin/` | Revit 外掛 `LebrCarbon`：「LEBR」頁籤、「開始計算」按鈕、計算前檢查、結果視窗、匯出結果 JSON |
| `revit-addin/Preview/` | 在 Revit 外開啟兩個視窗並截圖，開發畫面用 |

安裝到 Revit 2027（裝好後重新啟動 Revit）：

```bash
powershell -ExecutionPolicy Bypass -File revit-addin/install.ps1
```

不開 Revit、直接看視窗：

```bash
dotnet run --project revit-addin/Preview -- . shots --interactive
```

「從 Revit 模型擷取數量」（組員 1 的模組三，初版）在 `revit-addin/Revit/`：

1. 按 LEBR →「專案設定」（第一次會自動建立參數）：加入 `LEBR_Code`（牆、窗、帷幕嵌板、樓板類型）、`LEBR_Code_Body`（外牆本體）、`LEBR_Exclude`（不計入），以及專案資訊的 `LEBR_WearClass`、`LEBR_Structure`、`LEBR_UseType`、`LEBR_I`、`LEBR_SaD`、`LEBR_Fu`、`LEBR_L`、`LEBR_D0`、`LEBR_Rs`、`LEBR_CSER`、`LEBR_LL`，與選填的 `LEBR_AFu`、`LEBR_Sp`、`LEBR_F` 等覆寫值。
2. 在類型與專案資訊填好後，按「開始計算」，預設從目前模型擷取。
   - 「代號 JSON」按鈕可把已填好的 `LEBR_Code`、`LEBR_Code_Body`、專案資訊與 `LEBR_Exclude` 匯出成 JSON，或套用到另一個模型（格式 `lebr-codes`，範例 第1組根目錄的 `測試模型_LEBR代號.json`）。類型依品類 + 類型名稱對應（族群名稱會因 Revit 語言不同而不同，所以不強制相符）；不計入元件先比元件 ID，再比同品類、同類型、同樓層。
3. 擷取規則：牆用 `HOST_AREA_COMPUTED`（已扣開口），窗用寬 × 高，帷幕嵌板用面積；沒填代號的結構性內牆與樓板視為主結構；樓地板面積優先用房間，沒有房間時用樓板（最高層樓板視為屋頂板）；Sp 由直線格線計算；F 由各層最大樓板外框計算 f1 × f2，f3 以 1.0 計。

另外也可以選手冊附錄三範例或載入數量檔。引擎用附錄三自動測試，結果與手冊相差 0.05% 以內（差異來自手冊中間值的四捨五入與筆誤，見 `計畫書/公式運算邏輯.md` 第三節）。

## 已知的手冊問題

以下是手冊本身的數字前後不一致（已對照原圖確認不是抽取錯誤）。JSON 目前保留手冊印的值並標記 `CHECK`，待團隊決定後寫進 `corrections.csv`。詳細內容見核對用 Excel 的「⚠ 需確認」頁。

| 項目 | 頁 | 問題 |
|---|---|---|
| P-LCC 石膏 | 53 | 分階段加總約 0.039，總量印 0.35 |
| P-LCC PE 防水布（kg） | 55 | 分階段加總 0.252，總量印 0.042 |
| P-LCC 門窗鋁框（㎡） | 52 | 小數尾差 0.025 |
| OW-05、OW-06 | 56 | 減碳量小數尾差 0.056 |
| CWW-G04-ALPVC | 58 | 新建碳排印 55.92，a+b = 54.92 |
| CWW-G04-SS | 58 | 受上一列影響，驗算差 1 |
| F-M-W06 | 61 | 更新碳排印 15.87，依更新次數應為 10.58 |
| F-S-G01 | 62 | 手冊未標「基準值」，依灰底與 ΔF=0 推定為基準 |
| F-S-G02、F-S-G03 | 62 | 減碳量與驗算差 1.55 |
| 木作地坪基準 | 35 / 60 | 內文寫「平鋪實木板、美耐板」，附表標「平鋪超耐磨地板」；資料依附表 |

## 核對紀錄

| 日期 | 核對人 | 範圍 | 結果 |
|---|---|---|---|
| | | | |
