# LebrCarbon — Revit 2026 LEBR 蘊含碳排評估外掛

依《低碳（低蘊含碳）建築評估手冊》2025 年版計算。**設計自主檢討用，非正式標示結果。**

## 建置與載入

1. 需要 .NET SDK（可編譯 .NET 10）。Revit 2026.5 起 API 為 .NET 10，Revit 專案目標為 `net10.0-windows`。
2. 若 Revit 不是裝在 `C:\Program Files\Autodesk\Revit 2026`，修改 `Directory.Build.props` 的 `RevitInstallDir`。
3. 關閉 Revit，在方案資料夾執行：
   ```
   dotnet build LebrCarbon.sln
   dotnet test
   ```
   建置後自動部署到 `%AppData%\Autodesk\Revit\Addins\2026\`。

## 功能區「低碳建築」

| 按鈕 | 說明 |
|---|---|
| 綁定參數 | 加入 LEBR_FactorId、LEBR_Scope、LEBR_RebarRatio、LEBR_EC，以及非主結構用的類型參數 **LEBR_BlccId** |
| **LEBR 評估** | 主功能。手冊四階段算法：主結構迴歸公式（式 1~4）、非主結構 B-LCC（式 11）、施工（式 12~13）、拆除廢棄（式 14~19）、分級（式 20~27、表 10） |
| 模型檢查 | 主結構元件材質完整性 |
| 數量檢核 | 材料數量 × 附錄一 P-LCC 係數，與迴歸公式的 CFs 交叉比對（手冊 4-1 節「BIM 模型估算」替代方法） |

## LEBR 評估使用流程

1. **建築參數**分頁：外掛已由模型帶入樓層面積（AFu、AFb、S、Sb、BH）、軸線跨距（Sp）、標準層平面（PAr、b）。
   請確認一樓樓層與樓層表勾選（屋頂層、屋突不計），再補上結構計算書參數 I、SaD、Fu、Rs、L、D0，以及構造類別、CSER、出挑係數 Rc。
2. **非主結構構件**分頁：在牆、窗、樓板、帷幕嵌板的類型參數 `LEBR_BlccId` 填入 B-LCC ID 後按「從模型帶入」，或直接手動新增。
   一個類型可填多個 ID（分號分隔），例如 RC 外牆貼磁磚 = `CW-0;OW-1`。基準案自動依各構件的比較基準產生。
3. 按**計算**：結果分頁顯示 EEC、EECc、CFR、等級與所有中間值；輸入自動存在模型旁的 `{模型名}.lebr.json`。
4. **匯出 Excel 計算書**：版面參照手冊附錄三，每一格都是公式（藍字輸入、黑字公式），可逐格查核或修改藍字重算；另有「非主結構明細」與「說明」工作表。不依賴外部套件，避免與其他 Revit 外掛的 DLL 衝突。
5. **匯出 CSV**：報告與構件明細。

## B-LCC ID 速查（完整清單見 blcc.LEBR-2025.json）

| 類別 | ID | 說明 |
|---|---|---|
| 一般外牆外裝（附表 2-1） | OW-1 ~ OW-6 | OW-1 RC 外牆貼磁磚（基準）、OW-4 金屬模板 RC 貼磁磚 … |
| 外窗（附表 2-2） | W-{玻璃}-AL / -PL / -WD | 鋁框（基準）／塑鋼框／硬木框；玻璃 1~15 依附表順序，例如 W-11-AL 5+5mm 膠合玻璃鋁框 |
| 透光帷幕外窗（附表 2-3） | CWW-{玻璃}-AL / -SS | 鋁或塑鋼框（基準）／不銹鋼框 |
| 不透光帷幕及一般外牆（附表 2-4） | CW-0 ~ CW-5 | CW-0 傳統 15cm RC 外牆（基準） |
| 內隔間（附表 2-5） | IW-1 ~ IW-6 | IW-1 磚牆雙面粉刷（基準）、IW-2 輕質灌漿牆、IW-3 輕隔間 … |
| 室內地坪（附表 2-6） | F-G1~6、F-W1~6、F-N1~3、F-NW1~3 | 一般／木作／46-6 條分戶樓板 |
| 戶外地坪（附表 2-7） | P-RC1~8、P-GR1~7 | RC 基礎／碎石基礎 |

## 資料檔（src/LebrCarbon.Data/Resources）

- `blcc.LEBR-2025.json`：附錄二 B-LCC，121 個構件，含高／中／低耗損更新碳排與比較基準。
- `factors.LEBR-2025.json`：附錄一 P-LCC 混凝土、鋼筋、型鋼係數（數量檢核用）。
- `mapping-rules.json`：Revit 材質名稱 → 係數 ID（數量檢核用）。

## 驗證

`tests/LebrCarbon.Core.Tests/LebrExampleTests.cs` 以手冊附錄三「高雄市 Z 社會住宅」實例驗證，EEC、EECc、CFR（20.51%）、等級 1+ 均在 0.1% 內吻合。
已知手冊實例筆誤：戶外地坪 CFp3 更新碳排列為 108.38，小計實際採 48.28。
