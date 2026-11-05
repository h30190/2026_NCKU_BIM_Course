# ShaftChecker — 管道間維修空間檢討（Revit 外掛＋網頁儀表板＋AI）

成大建築資訊模型課程（115-1）期中專題。Revit 2024 外掛，自動檢討管道間的四組項目：
**防火時效及區劃、維修門與維修空間、管線分隔與間距、避開結構柱梁**（規則 A1–A9、P1–P5、S1–S2），
結果寫回 Room 參數、在 Revit 中上色與定位，並可從網頁儀表板或 Claude（MCP）操作。

> 組員開發流程、分支與 Pull Request 規則請看 **[CONTRIBUTING.md](CONTRIBUTING.md)**。

## 快速開始

| 步驟 | 做法 |
| --- | --- |
| 1. 安裝環境 | Revit 2024、[.NET 8 SDK 以上](https://dotnet.microsoft.com/download)、Git（或 GitHub Desktop）；要用 AI 對話再裝 [Python 3.10 以上](https://www.python.org/downloads/) 與 Claude 桌面版 |
| 2. 取得程式 | 用 GitHub Desktop clone 到**不會被 OneDrive／Google Drive 同步的資料夾**，例如 `C:\dev\ShaftChecker` |
| 3. 建置外掛 | 關閉 Revit，雙擊 `build-and-install.bat`（會先跑單元測試，再建置並部署到 `%AppData%\Autodesk\Revit\Addins\2024\`），結果記在 `build-log.txt` |
| 4. 開啟 Revit | 出現安全性提示選「一律載入」，功能區多一個「管道間檢討」頁籤 |
| 5. 網頁儀表板 | Revit 開著模型時，瀏覽器開 `http://localhost:5099/`（或按頁籤上的「檢討儀表板」按鈕） |
| 6. AI 對話（選用） | 雙擊 `setup-mcp.bat`，完全結束再重開 Claude 桌面版 |

## 測試模型

`.rvt` 檔太大，**不放在 GitHub**，統一放在組內共用雲端：

- 共用資料夾：**（請組長貼上連結，例如 OneDrive／Google Drive 共用資料夾網址）**
- `model1.rvt`：實際案例模型（含連結模型，打開後到「管理 → 管理連結」確認都已載入）
- 也可以在 Revit 按「建立測試模型」，在空白專案自動產生 T1–T6 六個測試案例（見下方〈T1–T6 測試案例〉）

## 系統架構

```
                       ┌── 網頁儀表板 ui/index.html（瀏覽器）
Revit 外掛 ── 本機 API ─┤
（讀取模型、規則判定、    └── MCP 伺服器 mcp_server/server.py ── Claude 桌面版（AI 對話）
  寫回、上色、定位）  http://localhost:5099
```

- **規則引擎（Core）**不引用 Revit API，所有合格與否都在這裡判定，可以不開 Revit 直接跑單元測試。
- **Revit 外掛**負責從模型擷取資料（含連結模型）、寫回參數、上色、選取定位，並內建只對本機開放的 HTTP 服務
  （HttpListener＋ExternalEvent，把請求交給 Revit 主執行緒執行）。
- **網頁儀表板**與 **MCP 伺服器**是兩個並列的用戶端，共用同一組 API 與同一套規則；AI 只負責理解問題與說明結果。

## 資料夾與分工

| 路徑 | 內容 | 開發時需要 Revit？ |
| --- | --- | --- |
| `src/ShaftChecker.Core/Rules/` | 檢討規則 A1–A9、P1–P5、S1–S2，`RuleEngine`、法規條文 `RuleGroups.cs` | 否 |
| `src/ShaftChecker.Core/Analysis/` | 管線分類、結構 2D 分析（禁區差集、移動建議）、穿梁檢討 | 否 |
| `src/ShaftChecker.Core/Recognition/` | 管道間多訊號辨識（評分） | 否 |
| `src/ShaftChecker.Core/Geometry/`、`Models/`、`Config/` | 幾何工具、資料模型、設定檔讀取 | 否 |
| `tests/ShaftChecker.Tests/` | xUnit 單元測試（規則、幾何、辨識） | 否 |
| `src/ShaftChecker.Revit/Collectors/` | 從模型擷取管道間、牆、門、管線、梁柱（含連結模型） | 是 |
| `src/ShaftChecker.Revit/Services/` | 本機 API 的實作（檢討、辨識、定位選取、測試模型） | 是 |
| `src/ShaftChecker.Revit/Bridge/` | 本機 HTTP 服務與 JSON | 是 |
| `src/ShaftChecker.Revit/Commands/`、`Writers/`、`App.cs` | 功能區按鈕、寫回參數、上色、CSV | 是 |
| `src/ShaftChecker.Revit/config/shaft-rules.json` | 門檻、關鍵字、要執行的規則 | 否 |
| `src/ShaftChecker.Revit/ui/index.html` | 網頁儀表板（單一檔案：HTML＋CSS＋JS） | 否（可用假資料預覽） |
| `tools/ui-preview/` | 產生儀表板假資料預覽頁，不開 Revit 也能改介面 | 否 |
| `mcp_server/` | MCP 伺服器與 Claude 桌面版註冊腳本 | 是（要實際呼叫時） |

## 建置與除錯

- 平常用 `build-and-install.bat` 即可。用命令列：
  ```
  dotnet test tests/ShaftChecker.Tests
  dotnet build src/ShaftChecker.Revit -c Debug -p:RevitVersion=2024
  ```
- Revit 會鎖住 DLL，**建置前一定要關閉 Revit**。
- 用 Visual Studio 除錯：開 `ShaftChecker.sln`，在 ShaftChecker.Revit 專案按 F5，會依 `Properties/launchSettings.json` 啟動 Revit 2024
  （Revit 裝在其他路徑時改這個檔，但不要提交自己的路徑）。
- 規則邏輯先用單元測試驗證，不用每次開 Revit。
- 只改網頁儀表板時不用重新編譯，但要再跑一次 `build-and-install.bat` 把網頁部署過去，瀏覽器按 Ctrl＋F5 重新整理。

## 在 Revit 中使用

1. **設定參數**：每個專案執行一次。建立 PS_淨寬、PS_淨深、PS_淨面積、PS_檢討結果、PS_違規項目、PS_檢討時間（房間）與 遮煙性能（門）。
2. **整理模型資料**
   - 每個管道間放 Room。名稱或編號寫 PS、DS、EPS、WS、「管道間」最保險；漏標也沒關係，外掛會用多種線索辨識（見下一節）。
   - 牆類型、門類型的「防火時效／防火等級」填寫，例如 `1 hr`、`2小時`。
   - 維修門勾選「遮煙性能」。
   - 維修門另一側的房間要放 Room（走廊、樓梯間等），A4 才判斷得到。
3. **管道間清單**：先確認辨識到的管道間、牆、門資料是否齊全。
4. **一鍵檢討**：在平面圖執行。違規紅色、資料不足黃色、警告橘色；違規元件會被選取；CSV 存到「文件\ShaftChecker\」。
5. **清除上色**：還原本次上色。

## 檢討項目

| 分組 | 規則 | 內容 | 性質｜依據 |
| --- | --- | --- | --- |
| 防火時效及區劃 | A1 | 管道間牆體防火時效 ≥ 1 hr | 法規｜設計施工編 第79-2條 |
| | A2 | 維修門防火時效 ≥ 1 hr 並具遮煙性能 | 法規｜設計施工編 第79-2條 |
| | A4 | 維修門不得開向樓梯間或排煙室：門另一側房間以 `get_FromRoom(phase)`／`get_ToRoom(phase)` 取得，名稱或「空間類型」參數符合即違規 | 法規｜設計施工編 第97條 |
| | 地下層 | A1、A2、A4 照常檢討，依據加註地下建築物條文 | 設計施工編 第203條（第十一章） |
| 維修門與維修空間 | A6 | 完成面邊界最小外接矩形：短邊＝淨寬、長邊＝淨深；面積取 Room.Area | 設計參數｜設計施工編 第246條 |
| | A7 | 讀門的「淨寬」「淨高」參數；沒有時以寬度、高度扣除門框寬度推算 | 設計參數 |
| | A8 | 門外側作業區內不得有障礙物 | 設計參數 |
| | A9 | 門頂梁下淨高：投影落在門洞上的梁，梁底到門檻須 ≥ 門高 + 預留 | 設計參數 |
| 管線分隔與間距 | P1 | 電氣類（Conduit、Cable Tray）與水類管線同一管道間時，兩群之間須有隔牆 | 法規｜建築設備編 第2-1條 |
| | P2 | 立管兩兩中心距扣除半徑與保溫層＝淨距 | 設計參數 |
| | P3 | 維修門正後方（管道間內）作業區不得有管線 | 設計參數 |
| | P4 | 高層建築物：瓦斯管與給排水以外的系統同管道間即違規（法條原文為「應單獨設置」，可在設定檔改成完全單獨） | 法規｜設計施工編 第246條 |
| | P5 | 風管穿出管道間牆處，附近須有防火閘門族群（名稱或「防火閘門」參數） | 法規｜建築設備編 第92、94條；設計施工編 第85條 |
| 避開結構柱梁 | S1 | 梁柱投影侵入管道間：遮擋面積、梁底到樓板淨高、扣除後可用淨尺寸；立管穿過梁柱；提出可移動方向與最小距離 | 設計參數 |
| | S2 | 管線穿梁：孔徑 ≤ 梁深 1/3、孔心在梁深中央 1/3、距柱面 ≥ 2 倍梁深、孔距 ≥ 3 倍孔徑；局部撞梁或沿梁內配置時建議下移距離 | 設計參數（結構實務，以結構技師要求為準） |

### 結構柱梁怎麼分析
1. 收集管道間周圍（預設 1.5 m 內）本層的梁、柱，含連結的結構模型；梁以實體頂點投影出平面矩形與梁底、梁頂標高。
2. 梁柱投影視為禁區，與管道間完成面邊界做 2D 交集，得到遮擋面積與侵入深度（小於容許值 2 cm 視為建模誤差）。
3. 以管道間自己的座標系網格取樣做差集，求扣除禁區後最大的矩形可用空間。
4. 沿管道間長、短邊與每根梁的法線方向平移，求完全避開所有禁區的最小距離（列出最短的三個方向）；立管則在管道間內求移出梁柱的最小距離。
5. 方向以平面圖表示：+X 往右、+Y 往上。
6. 專案（含連結）找不到任何結構梁柱時，A9、S1、S2 會顯示警告，請連結並載入結構模型。

## 管道間怎麼辨識（多訊號評分）

不再只看房間名稱。每個 Room 都會依下列線索得分，並附上判斷理由：

| 線索 | 分數 |
| --- | --- |
| 名稱／編號符合 PS、DS、EPS、WS、管道、管井、Shaft… | +100 |
| 房間任一文字參數（備註、部門、用途…）提到管道間 | +60 |
| 有 Shaft Opening（垂直開口）貫穿此位置 | +40 |
| 內有機電元件：風管、水管、電管、電纜架、機電設備（垂直管線加分） | 最多 +40 |
| 尺寸小而窄（≤4 m² 最高；短邊 ≤1200 mm 再加分） | 最多 +35 |
| 其他樓層在相同位置有相近大小的房間（貫穿樓層的特徵） | 最多 +20 |
| 名稱明顯是別的空間（廁所、辦公室、走廊、機房…） | −60 |
| 有 3 扇以上的門 | −15 |
| 面積超過 12 m² 且名稱沒寫管道間 | 不會被判成管道間 |

分數對應等級：**確定**（≥90，且有名稱／編號／參數證據，或你已確認）、**很可能**（≥60，預設納入檢討，標註「推測」）、
**疑似**（≥40，需要你確認，預設不納入）、非管道間。沒有名稱／參數證據、只靠尺寸與機電元件推測的，最高到「很可能」。

- 疑似清單在儀表板最上方，每筆有理由；按「納入檢討」「不是管道間」，決定存在本機（`%AppData%\ShaftChecker\confirmations.json`），不改動模型。
- 同時會列出：名稱像管道間但房間沒放置或沒封閉的房間、有 Shaft Opening 卻沒有房間涵蓋的位置（可能漏放房間）。
- 主模型與已載入的連結模型都會掃描房間、機電元件與 Shaft Opening。連結模型中的管道間可以檢討，但無法寫回參數、上色或選取（只能縮放定位）。
- 門檻與關鍵字可在 `config/shaft-rules.json` 調整：`shaftNamePatterns`、`shaftKeywordPatterns`、`nonShaftNamePatterns`、
  `detectConfirmedScore`、`detectLikelyScore`、`detectCandidateScore`、`maxShaftAreaM2`、`includeCandidateShafts`。
- 評分權重寫在 `ShaftClassifier.cs`，並有單元測試。

## 調整門檻

修改 `%AppData%\Autodesk\Revit\Addins\<年份>\ShaftChecker\config\shaft-rules.json`，不用重新編譯，下次執行即生效。

| 欄位 | 預設 | 說明 |
| --- | --- | --- |
| minFireRatingHours | 1.0 | A1、A2 防火時效下限（法規） |
| minClearWidthMm | 600 | A6 管道間淨寬下限 |
| minClearAreaM2 | 0 | A6 淨面積下限，0 不檢查 |
| minDoorClearWidthMm | 600 | A7 維修門淨寬下限 |
| minDoorHeightMm | 1800 | A7 維修門高度下限 |
| frontClearanceDepthMm | 750 | A8 門前作業區深度 |
| frontClearanceHeightMm | 2000 | A8 門前作業區高度（梁低於此高度會被抓到） |
| minClearDepthMm | 0 | A6 淨深下限，0 不檢查 |
| doorFrameDeductionMm / doorFrameHeadDeductionMm | 0 | A7 沒有淨寬、淨高與門框寬度參數時的扣除值 |
| doorHeadAllowanceMm | 50 | A9 梁底至少比門頂高出的距離 |
| minPipeClearMm | 50 | P2 立管淨距；S1 立管移位時與梁柱的間距 |
| doorBackClearDepthMm | 600 | P3 門後方作業區深度 |
| damperSearchMm | 1000 | P5 穿牆點附近找防火閘門的距離 |
| gasAllowedGroups | ["給排水"] | P4 可與瓦斯管同管道間的系統；空清單＝完全單獨 |
| highRiseMode | auto | 高層建築物判斷（auto：16 層或 50 m 以上；yes／no 強制） |
| basementLevelPatterns / basementElevationMm | B1…／−500 | 地下層判斷（樓層名稱或標高） |
| roomTypeParams | 空間類型… | A4 讀取的房間「空間類型」參數名稱 |
| structOverlapTolMm | 20 | S1 梁柱侵入容許值 |
| beamHoleMaxRatio / beamHoleEndDistFactor / beamHoleSpacingFactor | 1/3、2、3 | S2 穿梁限制 |
| enabledRules | 全部 | 要執行的規則 |

設計參數類的數值只是起始值，報告書中請註明採用的依據。

## T1–T6 測試案例

| 案例 | 做法 | 預期 |
| --- | --- | --- |
| T1 合格 | 1 hr 牆、1 hr 遮煙門、800×1200、門開向走廊 | 全部合格 |
| T2 | 一面牆改成無防火時效的類型 | A1 違規 |
| T3 | 門的遮煙性能取消勾選 | A2 違規 |
| T4 | 維修門開在樓梯間牆上 | A4 違規 |
| T5 | 管道間短邊 450 mm | A6 違規 |
| T6 | 門外 500 mm 處放一根柱 | A8 違規 |

## 網頁儀表板

Revit 開著模型時，瀏覽器開 `http://localhost:5099/`：

- 上方是檢討摘要與「建議下一步」；統計卡、四個分組卡可點選篩選。
- 點分組卡會依檢討項目分類列出每一處問題；點任一列，Revit 會切到該層平面圖、縮放並選取相關元件。
- 展開管道間可看到平面示意圖（梁柱投影＝禁區、立管、建議移動方向）。
- 左側面板可拖曳右緣調整寬度，雙擊收合。
- 門檻可直接輸入（長度單位 cm），調整後執行即為「假設分析」，不會寫回模型。

不開 Revit 改介面：`python tools/ui-preview/build_preview.py`，會產生帶假資料的 `tools/ui-preview/output/ui-preview.html`。

## AI 自然語言檢討（Claude 桌面版 + MCP）

```
Claude 桌面版 ─MCP(stdio)→ mcp_server/server.py ─HTTP localhost:5099→ Revit 外掛 → 規則引擎
```

判定一律由規則引擎完成，AI 只負責理解問題、呼叫工具與說明結果。

### 安裝（一次；專案資料夾搬家後要再執行一次）
1. 關閉 Revit，執行 `build-and-install.bat`。
2. 執行 `setup-mcp.bat`：安裝 Python 套件 `mcp`，並把 `shaft-checker` 加入 Claude 桌面版設定檔（原設定會備份為 `.bak`，其他 MCP 伺服器不受影響）。
3. 完全結束 Claude 桌面版（系統匣右鍵 → 結束）後重新開啟。

### 確認連線
1. 開啟 Revit 與模型，按「管道間檢討 → AI 連線狀態」。
2. 瀏覽器開 `http://localhost:5099/status`，看到 `"ok":true` 即可。
   必須用 `localhost`，不能用 `127.0.0.1`。

### MCP 工具

| 工具 | 功能 |
| --- | --- |
| revit_status | 確認 Revit 與模型 |
| get_rules | 規則、法規依據、門檻、需填參數 |
| list_shafts | 盤點管道間與資料完整度（含辨識等級、分數、理由） |
| find_shaft_candidates | 列出確定／很可能／疑似的空間與理由、孤立的 Shaft Opening、未放置的房間、尚未載入的連結 |
| confirm_shaft | 記錄使用者「是／不是管道間」的決定 |
| check_shafts | 執行檢討；可指定樓層/編號/規則；`overrides` 做假設分析 |
| highlight_issues / clear_highlight | 違規上色 / 清除 |
| select_element | 切換到該層平面圖、縮放並選取管道間或某一項問題的元件（含連結模型） |
| export_report | 匯出 CSV |
| build_test_model | 在目前專案建立 T1–T6 測試案例 |

### 展示問句
0. 「這個模型沒有標管道間，幫我找找哪些空間可能是？」
1. 「這個模型有哪些管道間？資料齊全嗎？」
2. 「幫我檢查全部管道間，並說明違規原因與法規依據。」
3. 「PS-2 為什麼不合格？帶我去看。」
4. 「如果淨寬標準提高到 800 mm，結果會怎麼變？」
4-1. 「哪些管道間被梁擋到？應該往哪裡移、移多少？」
4-2. 「有沒有管線穿梁的位置不符合規定？」
5. 「把違規的標紅，然後匯出報表。」

### 用瀏覽器直接測試 HTTP 端點
- `http://localhost:5099/rules`
- `http://localhost:5099/shafts?level=1F`
- `http://localhost:5099/check?levels=1F&rules=A1,A2,A4`

## 目前限制

- 連結模型（唯讀）中的管道間可以辨識與檢討，但結果無法寫回參數、無法上色或選取。
- 「上下樓層疊合」與「機電元件落點」用重心／中心點判斷，不規則形狀可能有誤差；請看理由並以人工確認為準。
- 確認／排除的決定存在這台電腦，換電腦要重新確認。
- 上色紀錄只保留在本次 Revit 工作階段。
- A3（樓板判別）、開向共用部分、垂直連續性尚未實作。
- P1 只認「牆」為隔牆；用一般模型或面板做的隔板不會被認出。
- P5 只檢討風管；水管、電管穿牆的防火填塞無法從模型判斷。
- S1、S2 以梁的平面矩形與上下標高近似，斜梁、變斷面梁會偏保守；穿梁門檻屬結構實務，請以結構技師要求為準。
- 地下層一律加註地下建築物條文；第十一章只適用於「地下建築物」（地下街等），一般建築物地下層仍依第79-2條，判讀時請注意。
