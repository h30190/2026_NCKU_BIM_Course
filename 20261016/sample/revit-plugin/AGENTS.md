# AGENTS.md — RfiPlugin（RFI sample 外掛）

Revit C# 外掛，只做「擷取」：讀選取＋截圖＋視圖資訊 → POST 到後端。不存狀態、不呈現列表（那是後端／前端的事）。

## 建置與部署（先看有沒有裝對應版 Revit）

```powershell
# 學生（Revit 2024）：dotnet build -c Release.R24; .\install.ps1 -Version 2024
# 備課機（Revit 2026）：dotnet build -c Release.R26; .\install.ps1 -Version 2026
# 移除：.\uninstall.ps1 -Version XXXX（Revit 要先關，不然 DLL 被咬住）
```

- 本機沒裝該版 Revit 就 build 該配置（`HintPath` 指 `C:\Program Files\Autodesk\Revit $(RevitVersion)\`）。
- `RfiPlugin.addin` 的 `AddInId` 必須是合法 GUID（手編會被 Revit 退件，參考既有值不要重產）。
- 只用兩版共通 API（Selection、ImageExportOptions、PushButtonData、ActiveView），R24/R26 同一份碼。

## 檔案分工

| 檔案 | 幹嘛 |
|---|---|
| `App.cs` | Ribbon 分頁＋兩個按鈕，只掛載不寫邏輯 |
| `CreateIssueCommand.cs` | 「建立議題」：選取→截圖→視圖/專案名→表單→POST；回報人預設 `Application.Username` |
| `OpenIssueViewCommand.cs` | 「開議題視圖」：GET 列表→選一筆→UniqueId 找視圖（退回用名）→切視圖→亮顯元件 |
| `IssueForm.cs` | 輸入視窗（標題/說明/回報人/token）；可調大小靠 Anchor，不要手寫 Resize 數學 |
| `TokenStore.cs` | token 存 `%APPDATA%\RfiPlugin\token.txt`；只在 POST 成功後記 |
| `install.ps1`／`uninstall.ps1` | 部署／移除（UTF-8 BOM，PS 5.1 無 BOM 會誤判） |

## 一定會踩的坑

- `ExportRange` 不是 `ImageExportRange`；`Form`/`Control` 要全名（撞 `Autodesk.Revit.DB` 同名類別）。
- `ElementId` 用 `new ElementId(long)`；RevitAPI 引用設 `Private=false`（不要把 Autodesk DLL 拷進輸出）。
- `net48`＋x64；截图用 `ImageExportOptions`（`ExportRange.CurrentView`＋PNG）。
- 後端位址只出現在 `BackendBase` 常數（`http://localhost:8080`），改一個地方就好。
- WinForms 新控制項一律設 Anchor（拉大視窗不跑版）；對話框預設值放建構子參數，不要寫死。

## 驗證

- `dotnet build -c Release.Rxx` 0 錯誤（MSB3277 警告無害，是 Revit 常態）。
- 真機驗：重開 Revit → 建一筆議題 → 後端/圖台看得到；「開議題視圖」會切視圖＋亮顯。
- 換 DLL 前關 Revit；commit 風格沿用 repo（`feat(1016-sample): ...`）。
