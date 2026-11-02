# 第1組｜LEBR 低碳建築碳排外掛

在設計階段就從 Revit 模型算出建築的蘊含碳排與 LEBR 等級，再用網頁圖台看 3D 與圖表。
依內政部建築研究所《低碳（低蘊含碳）建築評估手冊》2025 年版。

**組員**：張晁誠、林以晴、黃啟煒、黃冠瑋

## 先看這個

👉 **[下載與使用教學](下載與使用教學.md)**：從下載、安裝到算出等級，6 步完成。

- 外掛下載：[GitHub 下載頁](https://github.com/017-YiChing/2026_NCKU_BIM_Course_Group-1/releases/tag/lebr-midterm-20261102)（支援 Revit 2024～2027）
- 圖台網站：<https://lebr-g1-carbon-dashboard.netlify.app/>

## 做了什麼

| 部分 | 內容 |
|---|---|
| LEBR 資料庫 | 把手冊附錄一 P-LCC（建材碳排係數）、附錄二構件做法、各表格整理成程式可讀的 JSON／CSV |
| Revit 外掛 | 專案設定表單、構件代號（可用 JSON 匯出／套用）、計算前檢查、依手冊四階段與七類構件計算 LEBR 等級；主結構自動用模型的混凝土與鋼筋量估算（CFsn = An × L + Bn × M） |
| 圖台網站 | 首頁 3D 模型依樓層與構件顯示碳排；Dashboard 有等級尺、圖表、數量與碳排報告、計算詳情、註記 |

## 資料夾

| 路徑 | 內容 |
|---|---|
| [`下載與使用教學.md`](下載與使用教學.md) | 給使用者的安裝與操作教學 |
| [`LEBR碳排外掛/`](LEBR碳排外掛/) | 外掛原始碼（`lebr-db/revit-addin`）、計算引擎與資料庫（`lebr-db/csharp`、`lebr-db/dist`）、安裝腳本、快速開始、交接文件 |
| [`LEBR碳排外掛/測試模型_LEBR代號.json`](LEBR碳排外掛/測試模型_LEBR代號.json) | 構件代號 JSON，用「代號 JSON →套用代號」一次填好 |
| [`LEBR圖台網站/`](LEBR圖台網站/) | 圖台網站原始碼 |

依課程規定，編譯好的外掛 DLL 與 `.rvt` 不放進 repo，改放在上面的 GitHub 下載頁。
