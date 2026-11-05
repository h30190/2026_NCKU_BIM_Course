# 逃生動線檢討台

第三組課程交件：從 Revit 擷取單樓層模型資料，由程式計算逃生路線與距離，再以本機網頁呈現檢討結果。

## 目前可使用的流程

1. 在 Revit 放置房間，核對樓層、階段與房間分類。
2. 用「選擇直通梯」指定本層要使用的樓梯及終點。無法自動偵測第一階時，由使用者在平面圖點選。
3. 按「擷取模型與缺漏清單」，填入檢討條件並匯出。
4. 外掛產生模型快照 JSON、使用設定及 `*-檢討網頁.html`。網頁內含資料與計算程式，可離線開啟或傳送給其他人查看。

目前網頁採唯讀條件；要改模型或檢討條件，請回到 Revit 重新擷取。資料保存在使用者選擇的資料夾，沒有自動雲端同步或完整覆核紀錄儲存。

## 建置、安裝與驗證

- 外掛來源、安裝與擷取說明：[plugin/README.md](plugin/README.md)。
- 使用者操作步驟：[plugin/使用說明.txt](plugin/使用說明.txt)。
- 本次交件範圍與驗證狀態：[交件檢查紀錄](plugin/validation/2026-11-05-交件檢查.md)。
- 企劃與四人分工：[開始這裡.md](開始這裡.md)。其中時程與待辦保留原始規劃，實作狀態以本頁及驗證紀錄為準。

在 `plugin/` 執行 `./package.ps1` 可建置 Revit 2024–2027 安裝包；建置機需要 Node.js、相容的 .NET SDK，以及本機 Revit API 或可取得參考套件的網路。安裝包解壓縮後執行 `install.bat`，再重開 Revit。不同版本使用各自的 DLL。

本專案的規則包與幾何涵蓋範圍仍需覆核。程式提供輔助檢討；未知資料保留未確認狀態。模型、DLL、安裝 ZIP 與產生的報告不包含在這份程式碼交件中。

## 資料夾內容

```
egress-review/
├─ 開始這裡.md              ← 先讀這份，選自己的角色
├─ AGENTS.md / CLAUDE.md    給 AI 助手看的規則
├─ check.js                 commit 前執行，只抓會造成 bug 的問題【不能改】
├─ ownership.json           誰主要負責哪些檔案【不能改】
├─ lock.json                接口檔案的指紋（改完接口執行 node check.js --relock）
├─ docs/
│   ├─ 計畫書.md            產品與技術計畫 0.1
│   ├─ 整合規則.md          只統一這 20 個會造成 bug 的點，其他自由
│   ├─ AI改程式流程.md      用 AI 改程式只統一的四件事
│   ├─ data-contract.md     資料填寫說明
│   └─ 分工與時程.md         每天做什麼、怎麼合併
├─ regs/                    【C】法規原文（§92、§93）、rules.json 規則包
├─ engine/
│   ├─ geometry.js          【B】最遠點、路線、距離、轉向角
│   ├─ rules.js             【C】法規判定、設計提示
│   └─ review.js            【共用】把兩者串成檢討批次
├─ src/page.html            【D】介面原始碼
├─ plugin/                  【A】Revit 外掛、建置安裝腳本與驗證紀錄
├─ data/                    三份範例 JSON
├─ make_samples.js          產生 data/ 範例
└─ build.js                 組出網頁
```

## 改完怎麼看結果

需要 Node.js 20 以上（課程本來就要裝）。在這個資料夾開終端機：

```
node build.js
```

再雙擊 `egress-review-standalone.html` 用瀏覽器打開。

**不要直接改** `egress-review.html` 和 `egress-review-standalone.html`。這兩份是 `build.js` 自動產生的，改了會被蓋掉。
