# 開窗成果:model_02 P1

- Revit 專案:model_02_P1_windows
- 氣象:Tainan(TWN_SOR_Tainan.593580_TMYx.2004-2018.epw),annual,僅統計 18–30°C、8–22 時;常見風向 N
- 方案:N-S,10 扇 M_Window-Casement-Double: 1400 x 1800mm
- 推薦原因:N-S 相對外牆開窗形成穿堂風,連線避開服務核;Tainan annual(僅統計 18–30°C、8–22 時的可開窗時數)風資料,常見風向 N。採 M_Window-Casement-Double 1400 x 1800mm 共 10 扇:N向牆(1664649)5 扇(方向分數 1.00,窗牆比 12.6%)、S向牆(1664841)5 扇(方向分數 0.73,窗牆比 12.6%)。所有窗皆避開正對服務核的牆段;法定有效通風面積(PoC 暫算)21.82 m² ≥ 辦公區 5% = 20.95 m²;EEWH VP=0.156(100 分);達標即止,不追求最大窗。
- 建窗:10/10 扇(pass 10、warning 0、fail 0)
- VC-006:pass(碰撞 pass,檢查人 組員4)

| # | 方位 | 牆 ID | 窗 ID | 狀態 | 窗扇 | 問題 |
|---|---|---|---|---|---|---|
| 1 | N | 1664649 | 1670366 | pass | interior |  |
| 2 | N | 1664649 | 1670381 | pass | interior |  |
| 3 | N | 1664649 | 1670391 | pass | interior |  |
| 4 | N | 1664649 | 1670401 | pass | interior |  |
| 5 | N | 1664649 | 1670411 | pass | interior |  |
| 6 | S | 1664841 | 1670421 | pass | interior |  |
| 7 | S | 1664841 | 1670431 | pass | interior |  |
| 8 | S | 1664841 | 1670441 | pass | interior |  |
| 9 | S | 1664841 | 1670451 | pass | interior |  |
| 10 | S | 1664841 | 1670461 | pass | interior |  |

## 檢核
- VC-001 確認房間屬辦公居室且採直接對外開口路徑:provisional_assumption
- VC-002 計算各窗 PoC 法定有效通風面積:provisional_assumption
- VC-003 有效通風面積是否達樓地板面積 5%:pass
- VC-004 計算 EEWH 自然通風潛力 VP 與得分:provisional_assumption
- VC-005 以統一單位估算方案風量:provisional_assumption
- VC-006 確認 Revit family、type、host wall、位置及碰撞狀態:pass
- VC-007 若改採機械通風，核對第102條最小通風量:not_applicable
- VC-008 建築師或門窗廠覆核 assumption:not_started

## 檔案

- `model_02_P1_windows.rvt`:建好窗的 Revit 模型(Revit 開啟後專案名稱是檔名,API 會用牆 ID 辨識為模型 02)
- `3_build_report.json` 裡的 `commands_file`、`source_proposals_file` 是當時 `output/` 的路徑,對應內容就是本資料夾的 `2_mcp_commands.json` 與 `1_selected_proposal.json`

## 尚待處理
- 法定有效通風面積、辦公區面積為 PoC 暫算,正式使用前須由建築師或門窗廠覆核(VC-008)