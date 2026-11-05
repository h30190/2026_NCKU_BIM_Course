# 第5組｜Green Passport 循環建材護照與門構件再利用

以 Revit 2024 門構件為示範，把 A 模型的構件資料建立成可審核的數位護照，依 B 模型需求進行配對、預留與替換，並保存來源與目標的追蹤紀錄。

## 操作流程

```text
A 模型擷取門 → 建立護照 → 人工審核 Approved
                                  ↓
B 模型確認需求 → 三態配對 → Reserved 預留 → 替換門族群與材質
                                                    ↓
                                   Allocated 配置紀錄與唯讀圖台
```

核心配對條件為門寬、門高、門扇厚度、開啟方式、開門方向、室內外用途、材質及有效通行淨寬。門寬與門高採固定 ±5 mm，淨寬採供應值大於等於需求下限。缺少資料時標示 Pending Review，不直接視為符合。

## 交件內容

| 路徑 | 內容 |
|---|---|
| [GreenPassport/](GreenPassport/) | C# Revit 外掛、Go／SQLite 後端、唯讀圖台與測試 |
| [快速開始](docs/快速開始.md) | 環境、建置、安裝與完整操作順序 |
| [驗證與排錯紀錄](docs/驗證與排錯紀錄.md) | 自動檢查、用途正規化、紅字／亂碼及替換外觀說明 |
| [模型與資料說明](docs/模型與資料說明.md) | 本機模型準備、空白資料庫重測與資料管理 |

## A、B 測試模型下載

[Google Drive 模型共享資料夾](https://drive.google.com/drive/folders/1hs0NVZTUX0AQ8MwTbN5Lx1MZO2URrYo0)

- [A模型_綠色護照來源.rvt](https://drive.google.com/file/d/1D1SSTzA3cIPQNH3ethnUoFHFDsjfcVgx/view?usp=drivesdk)：擷取、建立與審核來源門護照。
- [B模型_新建案.0001.rvt](https://drive.google.com/file/d/1UN5xZSQZQDU2gVBeUiW3oUwa_dl66ZQZ/view?usp=drivesdk)：目前共享的是 Revit 備份命名檔；下載後在 Revit 開啟，另存為 `B模型_新建案.rvt` 作為配對／替換測試副本。

請先下載到本機並保留原始副本，同時開啟 A、B 模型後依[快速開始](docs/快速開始.md)操作。模型下載與資料管理說明見[模型與資料說明](docs/模型與資料說明.md)。

## 本次完成的功能

- 擷取門的實體與類型參數，保留欄位來源與 UniqueId。
- 個別／批次人工審核，以及 Matched／Pending Review／Not Matched 三態配對。
- `外部出入口、外部、室外、Exterior` 在配對時視為相同用途，保留護照原始文字。
- 供應資格、預留及配置狀態管理；網站提供唯讀查詢。
- 從同時開啟的 A 模型載入實際來源門族群，不再只複製 B 模型舊類型。
- 護照明確指定白色時，將白色表面材質套用到門板參數；不變更門框與把手的材質參數。
- 提供「更新已配置門」指令，補正舊版配置的族群與材質並補登稽核。

## 執行與檢查

在 `GreenPassport` 資料夾，以 Windows PowerShell 執行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\build.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File .\revit-plugin\install.ps1 -Version 2024
powershell -NoProfile -ExecutionPolicy Bypass -File .\start-backend.ps1
```

重新開啟 Revit 後操作。完整步驟及需求見[快速開始](docs/快速開始.md)。本機圖台為 `http://127.0.0.1:8080/dashboard`，不是公開網站。

本次 Git 提交包含原始碼與文件；RVT 透過上方 Google Drive 連結提供，編譯產物、工作資料庫、個人備份及暫存檔不納入 Git。模型取得後需同時開啟來源 A 模型與目標 B 模型。資料宣告與模型配置不等同實體材料檢測或現場安裝完成。

本專案由課程 Revit 外掛範例與既有 BIM_Plugin 實作改作；授權沿用倉庫根目錄的 [MIT LICENSE](../../LICENSE)。
