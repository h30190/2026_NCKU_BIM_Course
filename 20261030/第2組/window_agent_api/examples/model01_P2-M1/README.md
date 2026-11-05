# 開窗成果:model_01 P2-M1

- Revit 專案:model_01
- 氣象:Tainan(TWN_SOR_Tainan.593580_TMYx.2004-2018.epw),annual,僅統計 18–30°C、8–22 時;常見風向 N
- 方案:N-E-S-W,14 扇 M_Window-Casement-Double: 1050 x 1350mm + M_Window-Casement-Double: 1400 x 1800mm
- 推薦原因:手動調整:N向牆 4 扇 M_Window-Casement-Double: 1050 x 1350mm、E向牆 3 扇 M_Window-Casement-Double: 1400 x 1800mm、S向牆 4 扇 M_Window-Casement-Double: 1050 x 1350mm、W向牆 3 扇 M_Window-Casement-Double: 1400 x 1800mm。法定有效通風面積(PoC 暫算)22.92 m² ≥ 辦公區 5% = 17.89 m²;EEWH VP=0.192(100 分);最大窗牆比 9.3%。
- 建窗:14/14 扇(pass 14、warning 0、fail 0)
- VC-006:requires_verification(碰撞 unknown,檢查人 未填)

| # | 方位 | 牆 ID | 窗 ID | 狀態 | 窗扇 | 問題 |
|---|---|---|---|---|---|---|
| 1 | N | 1664649 | 1668907 | pass | interior |  |
| 2 | N | 1664649 | 1668924 | pass | interior |  |
| 3 | N | 1664649 | 1668934 | pass | interior |  |
| 4 | N | 1664649 | 1668944 | pass | interior |  |
| 5 | E | 1664784 | 1668954 | pass | interior |  |
| 6 | E | 1664784 | 1668971 | pass | interior |  |
| 7 | E | 1664784 | 1668981 | pass | interior |  |
| 8 | S | 1664841 | 1668991 | pass | interior |  |
| 9 | S | 1664841 | 1669001 | pass | interior |  |
| 10 | S | 1664841 | 1669011 | pass | interior |  |
| 11 | S | 1664841 | 1669021 | pass | interior |  |
| 12 | W | 1664926 | 1669031 | pass | interior |  |
| 13 | W | 1664926 | 1669041 | pass | interior |  |
| 14 | W | 1664926 | 1669051 | pass | interior |  |

## 檢核
- VC-001 確認房間屬辦公居室且採直接對外開口路徑:provisional_assumption
- VC-002 計算各窗 PoC 法定有效通風面積:provisional_assumption
- VC-003 有效通風面積是否達樓地板面積 5%:pass
- VC-004 計算 EEWH 自然通風潛力 VP 與得分:provisional_assumption
- VC-005 以統一單位估算方案風量:provisional_assumption
- VC-006 確認 Revit family、type、host wall、位置及碰撞狀態:requires_verification
- VC-007 若改採機械通風，核對第102條最小通風量:not_applicable
- VC-008 建築師或門窗廠覆核 assumption:not_started

## 尚待處理
- 尚未記錄人工碰撞/牆體切割檢查(/api/review)
- MCP 無法存檔:請在 Revit 另存 rvt(建議檔名 model_01_P2-M1_windows.rvt),放進此資料夾
- 法定有效通風面積、辦公區面積為 PoC 暫算,正式使用前須由建築師或門窗廠覆核(VC-008)