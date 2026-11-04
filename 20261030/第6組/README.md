# 第 6 組｜LebrCarbon：Revit 低碳建築（LEBR）蘊含碳排評估外掛

在 Revit 裡直接依內政部建築研究所《低碳（低蘊含碳）建築評估手冊》2025 年版，計算建築的蘊含碳排、碳排減碳率與 LEBR 等級，讓建築師在設計階段就能比較方案。

> 設計自主檢討用，非正式標示結果。正式標示仍須經評定專業機構審查。

## 做了什麼

- **從模型自動讀取**：樓層與樓地板面積（AFu、AFb、S、Sb、BH）、軸線跨距（跨距變化係數 Sp）、標準層平面周長與長寬比（形狀係數 F）。
- **手冊四階段算法**：
  - 主結構：迴歸公式，手冊式 1～7、表 3～8
  - 非主結構：面積 × 附錄二 B-LCC 單位碳排（式 8～11）
  - 施工、拆除廢棄（式 12～19）
  - 分級（式 20～27、表 10～12）
- **非主結構從模型帶入**：在牆、窗、樓板、帷幕嵌板類型的參數 `LEBR_BlccId` 填入構件 ID（例如 RC 外牆貼磁磚填 `CW-0;OW-1`），外掛自動加總面積，並產生基準案。
- **數量檢核**：另外以材料數量 × 附錄一係數估算主結構碳排，和迴歸公式交叉比對。
- **3D 熱點著色**：在 3D 視圖依每個構件的碳排量上色，從綠到紅共 5 級，一眼找出碳排集中的構件。構件碳排 = 主結構材料數量 × 附錄一係數 ＋ 非主結構面積 × B-LCC 新建碳排。以分位數分級，讓每級構件數大致相同，避免大樓板把其他構件都壓在最低一級。著色只改目前視圖的圖形覆寫，不改模型資料，按「清除著色」就能還原。
- **Excel 計算書**：版面參照手冊附錄三，每一格都是公式（藍字輸入、黑字公式），可逐格查核，或修改藍字直接重算。不依賴外部套件，避免與其他 Revit 外掛的 DLL 衝突。
- **PDF 摘要**：一頁 A4，列出等級、減碳率、八等級刻度、四階段碳排（設計案對基準案）、建築參數與非主結構明細，方便給業主或組員閱讀。以 Windows 內建「Microsoft Print to PDF」輸出，不帶第三方套件，中文字型由 Windows 處理。功能區「匯出報表」會依最近一次儲存的評估，一次匯出 Excel 計算書與 PDF 摘要。
- **AI 材料辨識**（`MaterialMapping/`，組員 simonlan0324-byte 製作）：AI 透過 Revit MCP 讀取模型的牆、窗類型，依規則比對 B-LCC 產生 `LEBR_BlccId` 草稿；在控制台「材料對照」分頁確認或修改後，再由 AI 寫回模型，省去手動查表填代碼。已用真實模型（18 種牆、19 種窗）跑過完整流程。判斷規則與 Revit MCP 接線見 `MaterialMapping/AGENTS.md`。
- **網頁控制台**（`console/`）：在評估視窗按「上傳到控制台」，把這次的評估存成快照；沒有 Revit 的人（業主、組員）打開網頁就能看到等級、減碳率、八等級刻度、四階段碳排與非主結構明細，也能勾選 2～3 個快照並排比較方案（例如輕隔間對磚牆），並下載該次的 Excel 計算書。架構沿用 20261016 RFI 範例：外掛擷取、Go 後端存取、React 前端呈現。碳排只在外掛的計算核心算一次，網頁不重寫公式，所以數字與 Excel 計算書一致。

## 驗證

計算核心用手冊附錄三「高雄市 Z 社會住宅」實例寫成單元測試，連同熱點分級、控制台快照、PDF 摘要內容等其他測試，46 個測試全部通過：

| 項目 | 手冊 | 外掛 |
|---|---|---|
| 評估範疇蘊含碳排 EEC | 22,756,875 kgCO2e | 0.1% 內吻合 |
| 碳排減碳率 CFR | 20.51% | 20.52% |
| 等級 | 1+ 級 | 1+ 級 |

差異來自手冊在中間步驟先四捨五入。驗證時另外發現手冊實例的兩處筆誤，記在 `LebrCarbon/docs/README.md`。

也在 Revit 中以 3 層測試建築實測，從模型讀取、帶入、計算到存檔的整條流程，結果都和手算一致。Excel 計算書匯出後以 LibreOffice 重算 89 個公式，零錯誤，數值與外掛一致。熱點著色在同一棟建築實測，為 70 個構件著色。其中主結構合計 35,770 kgCO2e，和「數量檢核」完全一致。清除著色在當次操作與重開 Revit 後都能正確還原。

網頁控制台以同一棟建築的評估輸入上傳快照，網頁顯示 1 級、CFR 18.05%、EEC 119,526 kgCO2e，與外掛及 Excel 計算書一致。後端另有 10 個 Go 測試（上傳、列表、格式版本拒收、範例匯入、方案比較、Excel 下載，以及材料對照的試算、跨專案同 ID 比對、檔案自動重讀）。

## 架構

```
LebrCarbon/
├─ src/LebrCarbon.Core/       計算核心（純 .NET，不引用 Revit API，可單獨測試）
├─ src/LebrCarbon.Data/       手冊資料庫（JSON）與專案存檔
│  └─ Resources/              blcc.LEBR-2025.json（附錄二，121 個構件）、factors.LEBR-2025.json（附錄一）
├─ src/LebrCarbon.Revit/      Revit 外掛：功能區、模型擷取、評估視窗
├─ src/LebrCarbon.Reports/    報告輸出（Excel 計算書、PDF 摘要內容、文字報告、CSV）
├─ src/LebrCarbon.Reports.Pdf/ PDF 摘要繪製（Windows「Microsoft Print to PDF」）
├─ src/LebrCarbon.Dashboard/  控制台傳送層：組快照 JSON、呼叫後端 API
├─ src/LebrCarbon.Cli/        lebr-snapshot：不開 Revit，由 .lebr.json 產生／上傳快照、匯出 PDF 摘要
└─ tests/                     單元測試（含手冊附錄三實例）
console/
├─ backend/                   Go + SQLite（127.0.0.1:8080）
├─ frontend/                  React + Vite（localhost:5173）
├─ samples/                   範例快照（方案 A／B），資料庫是空的時候自動匯入
└─ start.ps1                  一鍵啟動後端與前端
MaterialMapping/              AI 材料辨識（組員製作）
├─ AGENTS.md                  給 AI agent：Revit MCP 接線、B-LCC 判斷規則
└─ data/mapping.json          辨識結果，控制台「材料對照」分頁讀寫這個檔
```

Revit 相關程式全部集中在 `LebrCarbon.Revit`。手冊改版時只要換 `Resources` 裡的 JSON，Revit 改版時只要調整介接層。

## 建置與安裝

需求：**Revit 2026.5 以上**（API 26.5 以 .NET 10 編譯）、可編譯 .NET 10 的 .NET SDK。

> 課程教室環境是 Revit 2024 學生版，本外掛目前只支援 Revit 2026.5，需要在有 Revit 2026 的電腦上執行。

```
cd LebrCarbon
dotnet build LebrCarbon.sln
dotnet test
```

建置完成後會自動複製到 `%AppData%\Autodesk\Revit\Addins\2026\`。重新開啟 Revit，功能區會出現「低碳建築」頁籤。

Revit 若不是裝在 `C:\Program Files\Autodesk\Revit 2026`，請修改 `Directory.Build.props` 的 `RevitInstallDir`。

## 使用流程

1. 「低碳建築」頁籤 →「綁定參數」，把 `LEBR_` 共用參數加入專案。
2. 在外牆、窗、隔間、地坪類型填入 `LEBR_BlccId`，ID 清單見 `LebrCarbon/docs/README.md`。也可以請 AI agent 依 `MaterialMapping/AGENTS.md` 自動辨識，在控制台「材料對照」分頁確認後寫回模型。
3. 按「LEBR 評估」：
   - **建築參數**：確認模型讀到的樓層與面積，補上結構計算書的 I、SaD、Fu、Rs、L、D0。
   - **非主結構構件**：按「從模型帶入」。
   - 按「計算」，在「結果」分頁看 EEC、減碳率、等級與所有中間值。
   - 按「匯出 Excel 計算書」，產生可查核的 .xlsx 計算書（也可以匯出 CSV）；按「匯出 PDF 摘要」產生一頁式摘要。
   - 之後不開視窗也能匯出：功能區「匯出報表」依最近一次「計算」存下的評估，一次產生兩份檔案。
4. 切到 3D 視圖、視覺樣式設為「著色」，按「熱點著色」。對話框會列出圖例和碳排最高的 5 個構件。看完按「清除著色」還原。
5. 按功能區「輸出 → 網頁控制台」：控制台沒開就自動執行 `console\start.ps1`，網頁準備好後自動開瀏覽器（http://localhost:5173）。再在評估視窗按「上傳到控制台」，輸入快照名稱（例如「方案 B：輕隔間」）。

## 網頁控制台

需求：Go 與 Node.js（與 20261016 RFI 範例相同），不需要 Revit。

```
cd console
.\start.ps1
```

瀏覽器開 http://localhost:5173。資料庫是空的時候，後端會自動匯入 `console/samples/` 的兩筆範例快照，所以教室沒有 Revit 2026 也能 demo：

| 範例 | 內隔間 | 等級 | CFR |
|---|---|---|---|
| 示範方案 A | 輕隔間 IW-3（3 層測試建築實際做法） | 1 級 | 18.05% |
| 示範方案 B | 改為磚牆雙面粉刷 IW-1（其餘相同） | 1 級 | 16.14% |

兩筆都由外掛計算核心算出，附有 Excel 計算書。到「快照時間軸」勾選兩筆按「比較所選」，可看到內隔間被標為差異第 1（+2,798 kgCO2e）。要清空重來：關掉後端視窗，刪除 `console/backend/data/lebr.db`。

不開 Revit，也可以用評估輸入檔產生快照（需要 .NET SDK）：

```
cd LebrCarbon
dotnet run --project src/LebrCarbon.Cli -- 專案1.lebr.json --name "方案 A" --upload
```

`--out 檔名.json` 改為寫成檔案（可放進 `console/samples/`），`--excel` 一併附上 Excel 計算書。後端網址預設 `http://127.0.0.1:8080`，外掛端可在 `%AppData%\LebrCarbon\console.json` 以 `{"baseUrl": "…"}` 修改。

Revit 功能區的「網頁控制台」按鈕靠編譯時記下的 repo 路徑找 `console\start.ps1`；repo 搬家後重新編譯外掛，或在同一個 `console.json` 加 `"consoleDir": "D:\\…\\console"`。`start.ps1` 會略過已在運行的後端或前端，重複按不會開出第二份。

## 目前限制

- 不支援多棟分棟評估（手冊 3-5 節）、無標準層時逐層平均 F、對比模型結構分析法。
- 出挑係數 Rc 要手動輸入。停車場、設備室、儲藏空間的地坪，要避免標上 B-LCC ID。
- 熱點著色的構件碳排是材料數量法，用來找熱點，加總不等於 LEBR 評估的 EEC（主結構評估採迴歸公式）。
- PDF 摘要需要 Windows 的「Microsoft Print to PDF」印表機（Windows 10／11 內建）；沒有時外掛會提示，Excel 計算書不受影響。
- 係數庫管理介面、停駐面板、`LEBR_EC` 寫回構件尚未完成；尚未以事務所實際案例（含人工試算書）驗證。網頁控制台「從網頁回 Revit 定位構件」規劃中。

## 資料來源

`Resources` 中的係數整理自內政部建築研究所《低碳（低蘊含碳）建築評估手冊》2025 年版附錄一（ABRI P-LCC, 2022）與附錄二（建築構件碳排資料庫 B-LCC），著作權屬原出版單位。本專案僅作課程學習用途，對外發布或商業使用前，應向內政部建築研究所或台灣建築中心確認授權。
