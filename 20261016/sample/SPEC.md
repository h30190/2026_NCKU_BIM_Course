# RFI 議題追蹤圖台 sample：實作規格（骨架定版）

- 對應框架規格：[`../SPEC.md`](../SPEC.md)（願景）；本檔是骨架**已實作**的規格＋課堂決策紀錄
- 對象：coding 0 經驗學生＋coding agent 協作；課上拿去跑、拿去改
- 技術棧：Revit 外掛 C#（net48，傳統 `.addin`）／後端 Go＋SQLite（`modernc.org/sqlite`）／前端 React 19＋Vite

## Ch1 目標與非目標

- 目標：Revit 端一鍵擷取議題（選取＋截圖＋視圖＋專案名）→ 後端存 → 圖台多人共看（列表/看板/封存），點議題可在 Revit 開視圖＋亮顯元件
- 非目標：取代 CDE／正式審圖；無 IFC 幾何 viewer；無簽核流；第一版無 Due Date／匯出／批次操作

## Ch2 系統架構

```
Revit 2024/2026（C# 外掛，傳統 .addin 部署）
  │ POST multipart（議題＋截圖，Bearer token）／ GET 議題列表（開視圖用）
  ▼
Go 後端 :8080（REST＋SQLite 單檔，loopback 綁定）
  │ JSON（議題／帳號）／靜態截圖
  ▼
React 19 圖台（登入＋列表＋看板＋已封存＋使用者管理＋詳情抽屜）
```

- 切分：外掛只管擷取、後端只管存取、前端只管呈現（課上強制分層，見 `../README.md` 原則）

## Ch3 資料模型

issues 表（核心，其餘欄見程式 `backend/models.go`）：

- `id` PK、`title`、`description`、`status`（open／in_progress／closed，只能照此流轉）
- `reporter`（外掛預設 Revit Username，可改）、`assignee`（= username，後端驗證存在＋啟用）
- `source`（revit／ifc／manual）、`revit_element_id`、`ifc_global_id`、`model_id` FK（可空）
- `view_name`、`revit_view_id`（UniqueId，開視圖定位用）、`model_name`（樹狀分組；空則以後端 models 檔名 fallback）
- `screenshot`（檔名）、`position`（預留）、`archived`（0/1，封存只隱藏不刪除）
- `created_at`、`updated_at`

users 表：`username` PK、`password_hash`（bcrypt）、`role`（admin／member／viewer）、`active`、`created_at`
sessions 表：`token` PK、`username`、`created_at`
models＋elements 表：見框架 SPEC Ch3（IFC 上傳解析身分用）

## Ch4 權限矩陣

| 能力 | admin | member | viewer | 未登入 |
|---|---|---|---|---|
| 讀列表/詳情 | ✓ | ✓ | ✓ | ✓（課堂投影） |
| 新增/改狀態/指派/封存 | ✓ | ✓ | — | —（401） |
| 管使用者 | ✓ | —（403） | — | — |
| 上傳 IFC | ✓ | ✓ | — | — |

- 機制：`Authorization: Bearer <token>`；GET 讀取開放，寫入一律驗證（`requireAuth`）
- 啟動 seed `admin / changeme`（log 提醒改密碼）；外掛 token 存在本機 `%APPDATA%\RfiPlugin\token.txt`

## Ch5 後端 API

| 方法 | 路徑 | 權限 | 說明 |
|---|---|---|---|
| POST | `/api/login` | 公開 | `{username,password}` → `{token,username,role}` |
| POST | `/api/logout` | 登入 | 刪自己 token |
| GET | `/api/me` | 登入 | 回傳登入者 |
| GET/POST | `/api/users` | admin | 列表／新增 `{username,password,role}` |
| PATCH | `/api/users/:username` | admin | 改角色／啟停用 |
| POST | `/api/issues` | member | 新增（multipart：欄位＋screenshot 可選） |
| GET | `/api/issues` | 公開 | 列表；`status` 篩選；`archived` 預設 0、`1` 只看封存、`all` 全看 |
| GET/PATCH | `/api/issues/:id` | 讀公開／寫 member | 詳情；改狀態／指派（驗帳號）／封存 |
| POST | `/api/models` | member | 上傳 IFC（解析身分＋回 element_count） |
| GET | `/api/models/:id/file`、`/api/models/:id/elements` | 公開 | 下載 IFC／元件身分列表 |

## Ch6 前端圖台

- 登入頁（未登入只能看列表）；右上顯示帳號＋token（給外掛貼）＋登出
- 分頁：議題（表格）／看板（三欄拖曳改狀態，原生 DnD 無依賴）／已封存／使用者管理（admin）
- 左篩選面板：狀態／檔案／指派＋搜尋框；點標題右滑詳情抽屜（截圖、綁定、狀態、指派下拉、封存）
- viewer 全站唯讀（按鈕隱藏或禁用，後端也擋）

## Ch7 Revit 外掛（C#，雙配置 `Release.R24`／`Release.R26`）

- 「建立議題」：讀選取第一個 ElementId → 匯出使用中視圖截圖 → 記視圖名＋UniqueId＋專案名 → IssueForm（回報人預設 Username、token 記本機）→ POST multipart
- 「開議題視圖」：GET 議題列表選一筆 → 先 UniqueId 找視圖（退回用名）→ 切 `ActiveView` → 選中＋縮放綁定元件
- 部署：`dotnet build -c Release.Rxx` → `.\install.ps1 -Version XXXX`；移除：`.\uninstall.ps1`

## Ch8 測試腳本（`scripts/`）

- `smoke.ps1`：自起乾淨後端（臨時 port＋DB）打全鏈路 10 項（登入→建帳號→建議題→指派→封存→擋未登入/viewer）
- `regress.ps1`：後端 vet＋test、前端 build、外掛按本機 Revit 版本編（沒裝跳過）
- 後端另有 Go 單元測試（`go test ./...`：CRUD、auth、封存、IFC 解析）

## Ch9 課堂決策紀錄（為什麼長這樣）

1. 前端第一版不做 IFC viewer（列表＋詳情先行，端點留好）
2. 外掛用傳統 `.addin` 不用 Add-in Manager（10/02 已學會，全班同一套）
3. 後端綁 loopback（不跳防火牆；多人連同台才改 `:8080`）
4. `assignee` 存文字（= username，後端驗存在；不做 FK，學生好懂）
5. 議題綁視圖用名＋UniqueId 雙欄（名給人看，Id 給程式開）
6. 樹狀分組用 `model_name` 字串（不預註冊模型；IFC 用檔名 fallback）
7. ps1 一律 UTF-8 BOM（PS 5.1 無 BOM 會誤判編碼）
8. 學生版 Revit 產檔（與商業版不相容）；`.rvt`/DB/bin/obj 不進 repo

## Ch10 延伸路線

IfcOpenShell 幾何解析、ThatOpen viewer 點議題定位、Due Date 欄、匯出報表、批次操作、GitHub Issues 雙向同步、登入改密碼頁、token 過期。
