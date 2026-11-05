# AGENTS.md — 逃生動線檢討（BIM 課程分組作業）

給 AI 助手（Claude Code、Codex、Cursor 等）看的規則。回覆用繁體中文。

**原則：內容和做法盡量讓使用者自由發揮，只守住會造成 bug 的地方。**

## 一定要做

1. 改程式前先讀 `docs/整合規則.md`，不要違反裡面的 20 條。
2. 改完執行 `node check.js`。有「失敗」就修好；「提醒」轉告使用者就好。
3. 跟使用者說明你改了哪些檔案。

## 一定不能做

- 不改 `regs/D0070115_*.md`（法規原文）、`check.js`、`ownership.json`。
- 不憑記憶寫法規數字。法規數字只能來自 `regs/` 的原文；原文沒有的，標「需人工確認」，請使用者去全國法規資料庫查。
- 不在 `engine/` 裡呼叫 AI 或網路，也不要在對話中直接幫使用者判定合不合格。計算和判定都要寫成程式（計畫書第 1 節）。
- 不手改 `egress-review.html`、`egress-review-standalone.html`、`data/sample-3-*`，這些是 `node build.js` 自動產生的。

## 改之前要提醒使用者先在群組說

改到以下接口檔案時，提醒使用者這會影響其他組員：
`docs/data-contract.md`、`engine/review.js`、`build.js`、`data/sample-1-*`、`data/sample-2-*`。
可以改，改完要執行 `node check.js --relock`。

## 參考

- 角色與分工：`開始這裡.md`。使用者沒說角色也可以照常幫忙。
- 資料格式：`docs/data-contract.md`
- 計畫書：`docs/計畫書.md`
- 已決定的事：逃生路線用會流動的虛線，不加中間箭頭。
