# RFI 議題追蹤圖台：框架 SPEC（v1）

- 對應課程：10/16 Revit 外掛開發；學生 0 經驗＋agent 協作開發
- 目標：Revit 端一鍵擷取議題（截圖＋物件定位）→ 上圖台 → 多人共看一塊板子，點議題定位到模型（含 IFC）
- 技術棧：Revit 外掛 C#（Add-in Manager 載入）／後端 Go＋SQL／前端 React 19
- 原則：第一版只做最小可用；重的（幾何核心、真後端功能）列延伸

## Ch1 背景與目標

- 中央模型協作痛點：議題散在 LINE／口頭，對不回哪個物件、哪個位置
- 目標：議題一律綁模型物件（Revit ElementId 或 IFC GlobalId），圖台一點即定位
- 非目標：取代 CDE／正式審圖流程；第一版無帳號、無簽核

## Ch2 系統架構

```
Revit 2024（C# 外掛，Add-in Manager 載入）
  │ POST JSON（議題＋截圖）／上傳 IFC
  ▼
Go 後端（REST＋SQL）
  │ 存議題／存 IFC 檔／解析 IFC 身分資料
  ▼
React 19 圖台（議題列表＋詳情＋IFC 檢視定位，多人共看）
```

- 切分理由：外掛只管擷取、後端只管存取、前端只管呈現。框架僅為示範，各組做自己的題目，不沿用改。

## Ch3 資料模型

issues 表：
- id（PK）、title、description、status（open／in_progress／closed）
- reporter、assignee（純文字，第一版無帳號）
- source（revit／ifc／manual）
- revit_element_id（可空）、ifc_global_id（可空）、model_id（FK，可空）
- screenshot（檔名／路徑，可空）、position（JSON 文字，可空，預留）
- created_at、updated_at

models 表＋elements 表：
- models：id、filename、uploaded_at、element_count
- elements：model_id、global_id、type、name、storey（供定位＋列表，不存幾何）
- 狀態流：open → in_progress → closed（可退回）；第一版 last-write-wins，不處理衝突

## Ch4 Revit 外掛（C#）

- 載入：Add-in Manager（免寫 .addin、免重開 Revit；build 出 DLL 就能載，課上人人跑得起來）
- 指令「建立議題」：
  1. 讀目前選取（`Selection.GetElementIds`，取第一個；沒選就提示先選）
  2. 匯出使用中視圖截圖（`ImageExportOptions`）
  3. 小視窗輸入標題＋說明（WinForms 最簡兩欄）
  4. `HttpClient` POST 到 `POST /api/issues`（JSON＋multipart 截圖）
- 範圍外：批量建立、雙向寫回 Revit（列延伸）

## Ch5 後端 API（Go＋SQL）

- 建議：SQL 先用 **SQLite 單檔**（零設定）；driver 用 `modernc.org/sqlite`（pure Go，學生免裝 CGO）。Postgres 以後再說。
- 端點（最小）：
  - `POST /api/issues` 新增（multipart：欄位＋screenshot）
  - `GET /api/issues` 列表（query：status 篩選）
  - `GET /api/issues/:id` 詳情
  - `PATCH /api/issues/:id` 改狀態／指派
  - `POST /api/models` 上傳 IFC（存檔＋解析身分資料＋回 element_count）
  - `GET /api/models/:id/file` 下載 IFC（前端 viewer 用）
  - `GET /api/models/:id/elements` 元件身分列表
- IFC 解析（第一版）：不跑幾何核心；讀 STEP 文字抽 GlobalId＋類型＋名稱＋樓層（定位夠用）；IfcOpenShell 列延伸

## Ch6 前端圖台（React 19）

- 頁面：議題列表（狀態篩選）＋議題詳情（截圖、綁定資訊、狀態切換）＋模型頁（IFC 檢視）
- IFC 檢視建議：**ThatOpen（web-ifc 系）viewer**；點議題 → 以 GlobalId isolate／focus＋zoom
- 多人：共用同一後端即多人（第一版無帳號、無鎖定）
- 範圍外：登入、權限、簽核流（列延伸）

## Ch7 IFC 上傳與定位流程

1. 外掛或網頁上傳 .ifc → 後端存檔＋解析身分 → models 一筆
2. 開 RFI：選模型＋點選元件（viewer 點選回傳 GlobalId）或手填 GlobalId
3. 議題存 ifc_global_id＋model_id；圖台點議題 → viewer 載入模型 → focus 該 GlobalId
4. Revit 議題（ElementId）與 IFC 議題（GlobalId）共用 issues 表，source 區分

## Ch8 多人與中央模型對應

- 中央模型＝單一真相；圖台＝疊在上面的議題層（不碰模型檔，只記綁定）
- 第一版一致性：last-write-wins；衝突處理、權限、稽核列延伸
- 課堂對應：10/2 的 commit／PR 練程式碼協作；圖台練議題協作；兩邊都練到

## Ch9 非功能與限制

- 外掛：Windows＋Revit 2024（學生版可跑外掛；DLL 經 Add-in Manager 載入）
- 後端：`go run` 即跑；前端：`npm run dev` 即看（教室網路需能抓 Go module／npm，或預先 vendor／離線包）
- 授權：框架 MIT（跟課程 repo 一致）
- 第一版無帳號無加密，僅課堂／內網 demo 用

## Ch10 延伸（框架後續路線）

- IfcOpenShell 幾何解析（量體、碰撞提示）
- 圖台回控 Revit 定位（經 MCP：按議題 isolate 元件）
- GitHub Issues 雙向同步（議題即 issue）
- 登入＋權限＋簽核流
- 截圖標註（圈選＋箭頭）
- IFC 差異比對（兩版模型對議題的影響）

## Ch11 各組任務對應（10/30 發表）

- 各組做自己的 AI 應用，不沿用框架改；框架只看架構思路（Ch2 分層、Ch3 綁定設計可參考）
- 交件：照根 README「交件流程」PR（`20261030/第X組_應用名稱/`）

## Ch12 時程

- SPEC 定稿：現在（本檔）
- 框架最小可用（外掛擷取＋後端存取＋前端列表詳情＋IFC 上傳定位）：10 月第一週
- 10/16 課上 demo 框架（完整範例）；各組題目已定、照自己方向做；課綱細節另寫
