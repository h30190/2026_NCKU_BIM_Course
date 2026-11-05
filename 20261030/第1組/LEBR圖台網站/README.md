# LEBR 碳排圖台（網站）

網址：<https://lebr-g1-carbon-dashboard.netlify.app/>

把 Revit 外掛算出來的結果畫成 3D 模型與圖表。純前端靜態網站（HTML、CSS、JavaScript），不需要伺服器，也不會把資料送到任何伺服器。

## 頁面

| 頁面 | 檔案 | 內容 |
|---|---|---|
| 首頁 | `index.html`、`js/home3d.js`、`css/home3d.css` | 3D 模型依樓層與柱、梁、樓板、牆顯示碳排，點構件看計算；上方 About LEBR、Challenges、Features、Workflow、Team 說明（可用左右箭頭切換） |
| Dashboard | `dashboard.html`、`js/app.js`、`js/detail.js`、`js/report.js` | 總覽（KPI、LEBR 等級尺、材料／構件／樓層圖表）、模型說明、數量報告、碳排報告、計算詳情、註記、提醒與查核；可匯出 CSV 與報告書 |
| 共用 | `js/visitor.js`、`css/visitor.css` | 訪客登入視窗：只輸入名字，註記會顯示是誰寫的 |
| 共用 | `js/dropdown.js` | Dashboard 的下拉選單樣式 |

首頁外層是英文；點開的說明、構件詳情與 Dashboard 是中文。

## 資料怎麼進來

1. **Revit「在圖台開啟」**：外掛把結果 JSON 壓縮（gzip＋base64url）放在網址 `#d=` 後面，網站讀網址還原。`#` 後面的內容不會送到伺服器。
2. **載入 JSON**：外掛「匯出結果 JSON…」的檔案，用首頁「Load JSON」或 Dashboard「載入 JSON」開啟。
3. **上次的結果**：存在這台電腦的瀏覽器（`localStorage` 的 `lebr:last-result`）。開了好幾個分頁時，會依計算時間自動換成最新的結果。

看得懂的格式：外掛結果 JSON（含 `structure.bim` 估算明細）、`lebr-structure-result`、估算紀錄 `LEBR_EstimateJson`。

## 註記與訪客名字

- 名字與註記都只存在自己的瀏覽器；要給組員看，用「複製分享連結」或「匯出註記／匯入註記」。
- 名字不是帳號，沒有密碼。

## 自己架一份

1. 到 [Netlify](https://app.netlify.com/drop)，把整個 `LEBR圖台網站` 資料夾拖進去，就會拿到一個網址。
2. 讓外掛的「在圖台開啟」改開你的網址：在外掛資料夾（`%APPDATA%\Autodesk\Revit\Addins\<版本>\LebrCarbon\`）放一個 `dashboard_url.txt`，第一行寫你的網址。

本機測試：在這個資料夾執行 `python -m http.server 8000`，瀏覽 `http://localhost:8000/`。

## 致謝

版面與 Dashboard 架構來自組員的圖台原型，之後加上：外掛結果（含 BIM 估算明細）讀取、首頁 3D 依實際樓層顯示、多分頁同步最新結果、LEBR 簡介、英文首頁與 iOS 風格選單、說明頁左右切換、LEBR 等級尺、訪客登入與註記署名、自訂下拉選單。
