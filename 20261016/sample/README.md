# RFI 議題追蹤圖台 sample（＝課上骨架，拿去改）

對應教材：[`SPEC.md`](../SPEC.md)；課程說明見[上一層 README](../README.md)。
這一包就是骨架：課上各組先把它跑起來，再改成自己的題目。

## 課前裝好（缺一不可，先驗過）

| 工具 | 用途 | 驗證 |
|------|------|------|
| Revit 2024 學生版 | 外掛跑的地方 | 能開、能建牆 |
| .NET SDK（8 以上） | 編外掛 | `dotnet --version` |
| Go（1.23 以上） | 跑後端 | `go version` |
| Node.js LTS 20+ | 跑前端 | `node --version` |
| Git | 交件 | `git --version` |

## 跑起來（兩步）

```powershell
# 1. 圖台（開兩個視窗：後端 :8080＋前端 :5173）
.\start.ps1
# 瀏覽器開 http://localhost:5173

# 2. 外掛（教室電腦，Revit 2024）
cd revit-plugin
dotnet build -c Release.R24
.\install.ps1 -Version 2024
# 重開 Revit 2024 → "RFI Sample" 分頁 → 選一個元件 → 按「建立議題」
# 議題會記下當時視圖；要回去看按「開議題視圖」，選一筆即切到它的視圖
```

備課機是 Revit 2026：把上面 `R24` 換成 `R26`、`-Version 2026` 即可（csproj 雙配置已備好）。

## 要改什麼（地圖）

| 想改 | 動哪個檔 |
|------|----------|
| 議題欄位、狀態流 | `backend/models.go`＋`db.go`（改表），`handlers_issues.go`（改端點） |
| 後端位址、送出內容 | `revit-plugin/CreateIssueCommand.cs`（`BackendBase`） |
| 輸入視窗欄位 | `revit-plugin/IssueForm.cs` |
| 列表/詳情長相 | `frontend/src/App.jsx`，打 API 看 `frontend/src/api.js` |
| Ribbon 按鈕/分頁名 | `revit-plugin/App.cs` |
| 議題→開視圖邏輯 | `revit-plugin/OpenIssueViewCommand.cs`（先 UniqueId 找，退回用視圖名） |
| 樹狀分組（檔案/模型） | 後端 `model_name` 欄（外掛送專案名、IFC 用檔名）；前端 `App.jsx` 分組顯示 |

原則：外掛只管擷取、後端只管存取、前端只管呈現；要加功能先想它屬於哪一層。

## 封存（不刪除，只隱藏）

議題太多時在詳情按「封存」：列表預設不顯示，紀錄全留；要看按「顯示已封存」。
API：`PATCH /api/issues/:id` 帶 `{"archived": true}`；列表 `?archived=1` 只看封存、`?archived=all` 全看。

## 測試（改完先跑）

- 冒煙：`.\scripts\smoke.ps1`（起乾淨後端打全鏈路：登入→建帳號→建議題→指派→封存→擋未登入/viewer，共 10 項）
- 回歸：`.\scripts\regress.ps1`（後端 vet+test、前端 build、外掛按本機 Revit 版本編，沒裝就跳過）

## 常見卡關

| 症狀 | 解法 |
|------|------|
| 網頁說後端連不上 | 後端視窗還在跑嗎？先 `go run .` 再 `npm run dev` |
| port 被佔（:8080/:5173） | 關掉舊的黑視窗再跑 `.\start.ps1` |
| 外掛按鈕沒出現 | 重開 Revit；確認裝到 `Addins\2024`（`install.ps1` 會印路徑） |
| 送出說後端沒跑 | 外掛連的是 `localhost:8080`，後端視窗要開著 |
| Revit 開不了檔案 | 學生版與商業版不相容，一律用學生版產檔 |

## 交件

照根 README「交件流程」：組 fork → 改 `20261016/sample/`（或複製出去做自己的）→ PR 回主 repo，截止 10/29 23:59。
各組做自己的 AI 應用，不沿用這包改外觀；架構思路（分層、議題綁模型物件）可參考。
