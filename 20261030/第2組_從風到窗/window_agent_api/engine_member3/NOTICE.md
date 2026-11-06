# engine_member3

組員 3 的開窗推薦引擎，從交付包 `agent/` 原樣複製（`engine.mjs`、`data.mjs`、`epw.mjs`），沒有修改。
資料包透過 `lib/proposals.mjs` 呼叫 `generateProposals()`，資料來源改成 `data/` 底下的檔案。
原本的 `generate.mjs`、`agent.mjs`、`tools.mjs`、`test.mjs` 依賴交付包的資料夾名稱，因此沒有放進來。原始說明見 `ORIGINAL_README.md`。
