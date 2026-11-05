> ## 治理位階聲明（2026-08 收編時新增，非 Monstrare 原始內容）
>
> 1. **`CLAUDE.md` 是本專案（Revit MCP）唯一 canonical 的 AI 指令檔。** 本檔（`.agents/AGENTS.md`）與下方引用的 `ai/process/*`、`ai/skills/*` 所描述的 Monstrare 流程，若與 `CLAUDE.md` 的規則衝突或不一致，一律以 `CLAUDE.md` 為準。根目錄的 `AGENTS.md`（與本檔不同路徑）本身就是導向 `CLAUDE.md` 的 redirect，QA/QC `1-3` 檢查的是那份，不是這份。
> 2. **Monstrare 是選用（opt-in）的流程層，不是本專案的預設工作流。** 下面「本專案使用 Monstrare」一句要在此脈絡下理解：它描述的是「本專案裡某些工作（例如 PR #116 導入的綠建材 Epic-2，見 `tools/kanban/cards/TASK-003.json` 等 23 張卡）採用了 Monstrare 的看板／任務卡流程」，不是「每一次 AI 工作都必須先跑過 Monstrare 的 9 階段關卡」。沒有人明確要求走 Monstrare 流程時，不需要遵循它——直接依 `CLAUDE.md` 的 domain 觸發表或 `.claude/skills/` 對應 skill 工作即可，不必先做情境探索、規格書、mockup 關卡等 Monstrare 手續。`ai/skills/project-kickoff.md` 的 Epic 0（技術骨架、UI 設計系統五階段流程）針對的是「全新專案」，本專案是已上線、有 192 個 runtime tool／83 個 domain 檔／61 個 skill 的成熟專案，不適用、也不該被觸發。
> 3. **`ai/skills/*.md`（`design-craft.md`、`implementation-plan.md`、`project-kickoff.md`、`project-search.md`、`security-maintainability-review.md`、`spec-interrogation.md`、`test-verification.md`、`ui-mockup-gate.md`，共 8 個檔案）不是 Claude Code skill。** 它們不在 `.claude/skills/` 目錄下，不計入 `CLAUDE.md`「Current Source-of-Truth Counts」表格的 Claude skills 計數（目前 61），也不受 `domain/skill-authoring-standard.md` 的格式規範（frontmatter、觸發關鍵字表等）約束。觸發方式也不同：`.claude/skills/*/SKILL.md` 透過 `Skill` 工具呼叫；`ai/skills/*.md` 只是 Monstrare 流程內部互相引用的說明文件，沒有工具化觸發機制，讀取靠 Monstrare 文件裡的檔案路徑引用。
> 4. **來源**：這套 Monstrare 系統（`.agents/AGENTS.md` + `ai/` 35 檔 + `tools/kanban/` 25 檔 + `scripts/monstrare_mcp_server.py`）由 PR #116（作者 @CWLin0518）貢獻，隨綠建材工具鏈於 2026-08 一併收編進本 repo。
> 5. **兩份看板 HTML 內嵌相同的卡片資料，但本機儲存互不相通**：`tools/kanban/index.html` 與 `tools/green-material/GM_kanban.html` 內嵌的 `cardsData`（皆源自 `tools/kanban/cards/*.json` 的 23 張卡）逐欄位 byte-identical，唯一差異是各自的 `KANBAN_STORAGE_KEY`（分別是 `monstrare_cards_revit_green_v3` 與 `monstrare_cards_revit_green_v5_skill_table`），因此兩份頁面在瀏覽器 localStorage 裡維護獨立狀態，一份頁面的本機編輯不會出現在另一份。這是刻意的隔離設計，不是重複檔案，不要合併兩者的 storage key。
>
> **實測後的衝突盤點**：逐份讀過 `ai/process/workflow.md`、`ai/process/model-routing.md`、`ai/process/review-gates.md`、`ai/process/kanban.md`、`ai/process/definition-of-ready.md`、`ai/process/definition-of-done.md`、`ai/process/context-protocol.md` 與全部 8 個 `ai/skills/*.md` 之後，**沒有找到 Monstrare 對某個具體操作給出、且與 `CLAUDE.md` 明文相反指示的規則**（例如兩邊沒有對同一件事——如「要不要跑 `Transaction`」「commit 時機」「MCP 工具邊界」——給出互斥答案）。上面第 1、2 點講的是「哪一份文件優先」與「Monstrare 是不是預設」，屬於位階不明而非規則對撞。以下兩點則是讀過原文後找到的**具體潛在混淆點**，記在這裡以防被誤用：
> - `ai/process/model-routing.md` 用「風險／推理需求」抽象描述何時該用「高推理模型」（例：Claude Opus 級）或「快速通用模型」（例：Claude Sonnet 級），而 `CLAUDE.md`「MCP Registry Publish Consistency」一節把 `mcp-registry-sync` 與 `mcp-registry-ops-inspect` 兩支 agent **具名釘死在 Sonnet**（"Both agents must run as Sonnet (pinned in their frontmatter — do not override)"）。若照 `model-routing.md` 的抽象標準，把這兩支 agent 的審查工作當「安全性敏感的推理」升到 Opus 級模型，會直接牴觸 `CLAUDE.md` 的明文釘死規則。**這種情況一律以 `CLAUDE.md` 的具名釘死為準**，`model-routing.md` 的一般性路由建議不適用於這兩支已被指名的 agent。
> - `CLAUDE.md`「Subagent Delegation Boundaries」一節（含 2026-08-12 事故記錄）要求把保密邊界等專案級限制明寫進每則委派訊息，而 Monstrare 的 `ai/process/model-routing.md`／`ai/process/review-gates.md` 只泛泛談「審查 agent」的職責，完全沒有觸及這條規則——不是相反指示，是 Monstrare 文件對此議題保持沉默。用 Monstrare 流程委派子 agent 時，`CLAUDE.md` 這條規則仍然完整適用，不因為改走 Monstrare 流程而被取代或放寬。

# Agent 指令

本專案使用 Monstrare。請遵循
`ai/process/workflow.md` 中的共用流程。

## 操作規則

- 不得根據模糊的需求實作非小型（non-trivial）變更。
- 從情境探索（context discovery）與任務專屬的情境包（context pack）開始。
- 實作前使用 `ai/process/definition-of-ready.md`。
- 宣告完成前使用 `ai/process/definition-of-done.md`。
- UI 變更需要畫面規格與 mockup 決策紀錄（以 `ai/templates/screen-spec.md`、`ai/templates/mockup-decision.md` 為範本，產出到 `ai/artifacts/<Epic>/`），並先對照 `ai/context/design-system.md`：重用既有 design token 與元件，缺的元件照既有風格補做並登記回元件庫 inventory。
- 範本（`ai/templates/`）唯讀；所有填寫完成的產出物依 `ai/artifacts/README.md` 的慣例存放。
- 任何 mockup 或前端視覺實作，套用 `ai/skills/design-craft.md` 的設計工藝紀律，交付前對照 `ai/checklists/design-review-checklist.md`。
- Epic 0 的 UI 設計系統須依五階段（框架 → 風格 → design token → 元件庫 → 版面）分關卡展開，不得一步到位直接畫版面（見 `ai/skills/project-kickoff.md` 步驟 2a）。
- 高風險變更需要架構、安全性與測試審查關卡（review gate）。
- 優先採用既有專案模式，而非新增抽象層。
- 將變更範圍限制在已核准的任務卡（task card）內。
- 不得在未告知的情況下變更不相關的檔案。
- 提供驗證證據：指令、輸出結果、UI 的螢幕截圖，以及已知的殘留風險。
- **任務卡完成階段規則 (Lesson Learned)**：當完成任何任務 (Task) 的開發與驗證時，任務卡的階段 (Stage) 必須先更新為 `verify` (驗證中) 階段，**不得直接移至 `done` 階段**。必須由使用者手動測試成功後再進入 `done`。

## 必要流程

若是全新專案、還沒有 Epic/User Story 待辦清單，先執行 `project-kickoff` 流程，
把專案拆解成 Epic → User Story → Task 並建立看板卡片，再對每張任務卡套用下方流程。

1. 若 `ai/context/project-map.md` 存在，先閱讀它。
2. 若專案情境缺失或過時，執行 project-search 工作流程。
3. 對於新功能，建立或更新功能規格書。
4. 對於 UI 工作，產出多個 mockup 變體並等待人工選擇。
5. 產出 AI-ready 的任務卡。
6. 一次實作一張已核准的任務卡。
7. 執行驗證。
8. 執行審查關卡。
9. 彙整證據並在需要時請求人工驗收。

## 審查準則

審查程式碼時，優先關注：

- 功能性錯誤與回歸問題。
- 安全性與隱私風險。
- 身分驗證（auth）、權限、密鑰（secret）、檔案、網路與金流邊界。
- 資料驗證與錯誤處理。
- 可維護性、重複程式碼與架構偏移（architectural drift）。
- 缺失的測試或薄弱的驗證。

發現的問題應盡可能包含檔案與行號參照。
