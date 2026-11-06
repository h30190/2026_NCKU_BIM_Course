# 通風開窗 Agent

依「外牆方位與尺寸」「服務核位置」「任意地點的風資料」建議開窗位置、尺寸與窗型。只提出建議,不修改 Revit。

## 用法

```bash
node agent/generate.mjs --model 01                        # 一次只處理一個模型(01 或 02,預設 01),輸出 window_proposals_model01.json
node agent/generate.mjs --epw 路徑/TWN_xxx.epw            # 換成台灣其他地點(Climate.OneBuilding.org 的 TWN_*.epw)
node agent/generate.mjs --epw xxx.epw --months 6,7,8      # 只看夏季
node agent/generate.mjs --tmin 20 --tmax 28 --hours 9,17  # 自訂開窗條件
node agent/generate.mjs --all-hours true                  # 不篩選,統計全部時數
node agent/test.mjs                                       # 獨立驗證
ANTHROPIC_API_KEY=... node agent/agent.mjs "請推薦開窗方案"   # LLM 對話層
```

## 輸入契約

- 氣象:標準 EPW(風向第 21 欄、風速第 22 欄)。任何地點、任何牆面方位都會即時計算風向分數,不依賴預先算好的 JSON。
- `building_data.json`(組員 1 格式):每面牆需有 `is_exterior`、`orientation.azimuth_deg`(真北順時針)、`outward_normal_xyz`、`length_m`、`height_m`、`location_line`(含 `s_axis_unit_xyz`)、`available_window_intervals`。
- 服務核:`is_exterior=false` 的牆依端點相接自動分群,每群取外包矩形。
- 辦公區面積:有 Revit 辦公 Room 就直接用,否則由外牆封閉環內縮、減去各服務核外輪廓推算(假設直角多邊形)。

## 開窗時段篩選

冬季太冷、夏季太熱、非辦公時段不會開窗,這些時數不納入風向統計。預設只統計氣溫 18–30°C、8–22 時(含晚上加班時段的夜間通風;示意值,待團隊確認)。EPW 的「時」為該小時結束時刻(1–24);EPW 逐時氣溫缺值視為不開窗。輸出會附上各月可開窗時數,方便檢查某季是否幾乎沒有可用時數。

## 窗數與方案數

Agent 自動找出剛好滿足 5% 法定有效通風面積與 VP ≥ 0.10 的最少窗數,不追求最大窗。固定給 **3 個方案**(不同配置;配置不足 3 種時補上同配置的其他窗型)。一次只處理一個模型。

## 決策邏輯

1. 相對外牆(外法線內積 ≤ −0.7)各成一案,另有全部外牆合併案。
2. 窗只用模型已載入且可通風的完整 family:type,窗數與牆長成正比。
3. 位置:先排「不正對服務核」的牆段,放不下才排正對的;區間內等間距,保持窗間距。
4. 規則:法定有效通風面積 ≥ 辦公區 5%、窗牆比 ≤ 0.4(假設)、EEWH VP ≥ 0.10 即止。
5. 排序:方向分數(以穿堂風進風側為準)0.5、VP 達標度 0.4、避開服務核 0.1(示意權重);分數差在 0.03 內取窗數較少者。

## 限制

- 服務核以外包矩形處理,斜向或 L 形核心較保守。
- 垂直範圍、碰撞、3D 宿主切割未驗證;建窗後須回讀 type 與 Sill Height。
- 法定有效通風面積為 PoC 暫算,須由建築師或門窗廠覆核。
