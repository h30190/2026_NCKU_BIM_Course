// C 法規判定回歸測試：node engine/rules.test.js
const assert = require("node:assert/strict");
const { walkLimit, judgeRoom, judgeCorridor } = require("./rules.js");

const P = { "P-NEAR": 0.9, "P-TURN": 90, "P-CLEAR": 1.5 };
const room = { is_habitable: true };
const facts = (distance) => ({ distance_exact_m: distance, turns: [] });
const judge = (group, distance, extra = {}, f = facts(distance), p = P) => {
  const settings = { use_group: group, fire_resistant: true, floor_number: 3, ...extra };
  return judgeRoom(room, f, walkLimit(settings), p, []);
};

// §93 距離門檻：邊界符合，原始值超過就不符合。
assert.equal(judge("B-3", 30).status, "符合");
assert.equal(judge("B-3", 30.0001).status, "不符合");
assert.equal(judge("G-2", 50).status, "符合");
assert.equal(judge("G-2", 50.01).status, "不符合");
assert.equal(judge("C-1", 70, { tv_studio_with_audience: false }).status, "符合");
assert.equal(judge("C-1", 31, { tv_studio_with_audience: true }).status, "不符合");

// 樓層折減、非防火構造上限，以及未實作條件須保留人工確認。
assert.equal(judge("A-1", 20, { floor_number: 15 }).status, "符合");
assert.equal(judge("A-1", 20.001, { floor_number: 15 }).status, "不符合");
assert.equal(judge("G-2", 30, { fire_resistant: false }).status, "符合");
assert.equal(judge("G-2", 30.001, { fire_resistant: false }).status, "不符合");
assert.equal(judge("C-1", 30, { tv_studio_with_audience: null }).status, "需人工確認");
assert.equal(judge("G-2", 20, { duplex_housing: true }).status, "需人工確認");
assert.ok(walkLimit({ use_group: "G-2" }).rules.includes("R93-2-END"));

// 缺少資料不可誤判為通過；非居室由人工確認是否排除。
assert.equal(judgeRoom(room, { missing: "房間沒有門" }, walkLimit({ use_group: "B-3" }), P, []).status, "模型資料不足");
assert.equal(judgeRoom(room, { turns: [] }, walkLimit({ use_group: "B-3" }), P, []).status, "模型資料不足");
assert.equal(judgeRoom({ is_habitable: false }, facts(1), walkLimit({ use_group: "B-3" }), P, []).status, "需人工確認");
assert.equal(judge("B-3", 25, { fire_resistant: null }).status, "需人工確認");

// 未填樓層數、未確認的樓梯、自動推得的第一階：都要留給人確認，不能直接判符合。
assert.equal(judge("B-3", 10, { floor_number: null }).status, "需人工確認");
assert.equal(judge("B-3", 10, {}, { ...facts(10), stair_confirmed: false, stair_name: "樓梯 A" }).status, "需人工確認");
assert.equal(judge("B-3", 10, {}, { ...facts(10), stair_confirmed: true, endpoint_source: "auto:lowest_run_path_start" }).status, "需人工確認");
assert.equal(judge("B-3", 10, {}, { ...facts(10), stair_confirmed: true, endpoint_source: "user_confirmed:x" }).status, "符合");
assert.equal(judgeRoom({ is_habitable: false }, { missing: "房間沒有門" }, walkLimit({ use_group: "B-3" }), P, []).status, "需人工確認");
assert.equal(judgeRoom({ is_habitable: null }, { missing: "房間沒有門" }, walkLimit({ use_group: "B-3" }), P, []).status, "模型資料不足");

// 專案提示只提供改善提醒，不改變法規結論。
assert.equal(judgeRoom(room, { ...facts(27), turns: [] }, walkLimit({ use_group: "B-3", fire_resistant: true, floor_number: 3 }), P, []).status, "符合");
assert.ok(judgeRoom(room, { ...facts(27), turns: [] }, walkLimit({ use_group: "B-3", fire_resistant: true, floor_number: 3 }), P, []).hints.some((h) => h.id === "P-NEAR"));
assert.equal(judgeRoom(room, facts(27), walkLimit({ use_group: "B-3", fire_resistant: true, floor_number: 3 }), {}, []).status, "符合");
const turnResult = judgeRoom(room, { ...facts(10), turns: [{ at: [5, 0], deflection_deg: 90 }] }, walkLimit({ use_group: "B-3", fire_resistant: true, floor_number: 3 }), P, [{ name: "走廊 A", centerline: [[0, 0], [10, 0]], clear_width_m: 1.2 }]);
assert.equal(turnResult.status, "符合");
assert.ok(turnResult.hints.some((h) => h.id === "P-TURN"));

// §92 走廊寬度：類組、供教室使用條件與面積門檻。
const corridor = (width, both = true) => ({ element_id: "c1", name: "走廊", clear_width_m: width, rooms_both_sides: both });
const c = (w, settings, area, both = true) => judgeCorridor(corridor(w, both), settings, area);
assert.equal(c(2.4, { use_group: "D-3", corridor_serves_classroom: true }, 10).status, "符合");
assert.equal(c(2.39, { use_group: "D-3", corridor_serves_classroom: true }, 10).status, "不符合");
assert.equal(c(1.8, { use_group: "D-3", corridor_serves_classroom: false }, 10).status, "符合");
assert.equal(c(1.6, { use_group: "F-1" }, 10).status, "符合");
assert.equal(c(1.6, { use_group: "G-2", below_ground: false }, 200).status, "符合");
assert.equal(c(1.59, { use_group: "G-2", below_ground: false }, 200).status, "不符合");
assert.equal(c(1.2, { use_group: "G-2", below_ground: false }, 199).status, "符合");
assert.equal(c(null, { use_group: "G-2" }, 10).status, "模型資料不足");
assert.equal(c(1.2, { use_group: "D-3", corridor_serves_classroom: null }, 10).status, "需人工確認");

console.log("C rules regression tests passed");
