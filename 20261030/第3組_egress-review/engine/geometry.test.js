// B 路線淨距回歸：node engine/geometry.test.js
const assert = require("node:assert/strict");
const G = require("./geometry.js");
let passed = 0;
const test = (name, run) => { run(); passed++; console.log(`PASS ${name}`); };
const rect = (x1, y1, x2, y2) => [[x1, y1], [x2, y1], [x2, y2], [x1, y2]];
const L = [[0, 0], [8, 0], [8, 3], [3, 3], [3, 8], [0, 8]];
const near = (a, b) => assert.ok(Math.abs(a - b) < 1e-7, `${a} != ${b}`);

// 獨立抽查每段，不以 canWalk 自我驗證。淨距是到線段（不是只量角點）。
function wallDistance(p, poly) {
  return Math.min(...poly.map((a, i) => {
    const b = poly[(i + 1) % poly.length], dx = b[0] - a[0], dy = b[1] - a[1];
    const t = Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / (dx * dx + dy * dy || 1)));
    return Math.hypot(p[0] - a[0] - t * dx, p[1] - a[1] - t * dy);
  }));
}
function clearRoute(path, room, obstacles = []) {
  assert.ok(path && path.length >= 2, "缺少路線");
  for (let i = 1; i < path.length; i++) {
    const a = path[i - 1], b = path[i], steps = Math.max(1, Math.ceil(G.dist(a, b) / 0.02));
    for (let j = 0; j <= steps; j++) {
      const p = [a[0] + (b[0] - a[0]) * j / steps, a[1] + (b[1] - a[1]) * j / steps];
      assert.ok(G.inPoly(p, room) > 0);
      for (const poly of [room, ...obstacles]) assert.ok(wallDistance(p, poly) >= 0.45 - 1e-6, `淨距不足：${p}`);
      assert.ok(obstacles.every((o) => G.inPoly(p, o) < 0));
    }
  }
}

test("L 型路線在 0.45 m 內縮角轉彎，舊的貼牆角路線被拒絕", () => {
  assert.equal(G.canWalk([7.5, 0.5], [3, 3], L, []), false);
  const path = G.indoorPath([7.5, 0.5], [0.5, 7.5], L, []);
  assert.deepEqual(path, [[7.5, 0.5], [2.55, 2.55], [0.5, 7.5]]);
  near(G.plen(path), 2 * Math.hypot(4.95, 2.05));
  clearRoute(path, L);
});
test("順／逆時針外框得到同一距離", () => {
  const path = G.indoorPath([7.5, 0.5], [0.5, 7.5], [...L].reverse(), []);
  near(G.plen(path), 2 * Math.hypot(4.95, 2.05)); clearRoute(path, L);
});
test("斜牆與旋轉座標也維持淨距", () => {
  const angle = 0.37, rotate = ([x, y]) => [x * Math.cos(angle) - y * Math.sin(angle) + 37, x * Math.sin(angle) + y * Math.cos(angle) - 12];
  const room = L.map(rotate), path = G.indoorPath(rotate([7.5, 0.5]), rotate([0.5, 7.5]), room, []);
  near(G.plen(path), 2 * Math.hypot(4.95, 2.05)); clearRoute(path, room);
});
test("不靠移動起點掩蓋錯誤，合法起點保留原位", () => {
  const start = [6, 1.1], end = [1.2, 6], path = G.indoorPath(start, end, L, []);
  assert.deepEqual(path[0], start); assert.deepEqual(path.at(-1), end); clearRoute(path, L);
  assert.equal(G.indoorPath([7.9, 0.1], end, L, []), null);
});
test("45 cm 為新門檻：46 cm 與恰好 45 cm 可行，44 cm 不放行", () => {
  const room = rect(0, 0, 8, 8);
  assert.equal(G.canWalk([0.46, 1], [0.46, 7], room, []), true);
  assert.equal(G.canWalk([0.45, 1], [0.45, 7], room, []), true);
  assert.equal(G.canWalk([0.44, 1], [0.44, 7], room, []), false);
  clearRoute(G.indoorPath([0.46, 1], [0.46, 7], room, []), room);
});
test("柱子外擴，路線不再貼柱角", () => {
  const room = rect(0, 0, 10, 10), obstacles = [rect(4, 4, 6, 6)];
  assert.equal(G.canWalk([2, 5], [8, 5], room, obstacles), false);
  const path = G.indoorPath([2, 5], [8, 5], room, obstacles);
  assert.ok(G.plen(path) > 6); clearRoute(path, room, obstacles);
});
test("兩個端點都合格，斜線中途擦柱仍須被拒絕", () => {
  const room = rect(0, 0, 10, 10), obstacles = [rect(4, 4, 6, 6)];
  assert.equal(G.canWalk([3.5, 4], [4, 3.5], room, obstacles), false);
  clearRoute(G.indoorPath([3.5, 4], [4, 3.5], room, obstacles), room, obstacles);
});
test("半埋在牆內的柱子也保留 0.45 m 轉彎空間", () => {
  const room = rect(0, 0, 10, 10), obstacles = [rect(4, -1, 6, 2)];
  clearRoute(G.indoorPath([2, 1], [8, 1], room, obstacles), room, obstacles);
});
test("兩柱之間不足 0.9 m 時繞外側，不從膨脹範圍交疊處鑽過", () => {
  const room = rect(0, 0, 10, 10), obstacles = [rect(4, 4, 5, 6), rect(5.7, 4, 6.7, 6)];
  clearRoute(G.indoorPath([5.35, 2], [5.35, 8], room, obstacles), room, obstacles);
});
const neck = (width) => [[0, 0], [3, 0], [3, 2 - width / 2], [7, 2 - width / 2], [7, 0], [10, 0], [10, 4], [7, 4], [7, 2 + width / 2], [3, 2 + width / 2], [3, 4], [0, 4]];
test("0.8 m 狹頸不可繼續沿原路線穿過", () => {
  assert.equal(G.indoorPath([1, 2], [9, 2], neck(0.8), []), null);
});
test("1.2 m 通道仍然可以走", () => {
  const room = neck(1.2); clearRoute(G.indoorPath([1, 2], [9, 2], room, []), room);
});
test("恰好 0.9 m 的直通段兩側各保留 0.45 m", () => {
  const room = rect(0, 0, 10, 0.9); clearRoute(G.indoorPath([0.45, 0.45], [9.55, 0.45], room, []), room);
});
test("內縮範圍消失時不回退到未內縮路線", () => {
  assert.equal(G.indoorPath([0.4, 0.4], [5, 0.4], rect(0, 0, 6, 0.8), []), null);
  assert.equal(G.farthestPoint(rect(0, 0, 6, 0.8), [], [[0, 0.4]], [1]), null);
});
test("門口只允許最後的垂直接入短段", () => {
  const door = [0, 6], path = G.indoorPath([7.5, 0.5], door, L, []);
  assert.deepEqual(path.at(-2), [0.45, 6]); assert.deepEqual(path.at(-1), door);
  near(G.dist(path.at(-2), door), 0.45); clearRoute(path.slice(0, -1), L);
});
test("牆心內的門保留真實座標與距離，過遠端點不自動接通", () => {
  const room = rect(0, 0, 8, 8), door = [8.15, 4], path = G.indoorPath([4, 4], door, room, []);
  assert.deepEqual(path.at(-2), [7.55, 4]); assert.deepEqual(path.at(-1), door); near(G.plen(path), 4.15);
  assert.equal(G.indoorPath([4, 4], [10, 4], room, []), null);
});
test("門口豁免不能穿過柱子或其他牆", () => {
  const room = rect(0, 0, 8, 8);
  assert.equal(G.indoorPath([4, 4], [8, 4], room, [rect(7.6, 3.5, 8, 4.5)]), null);
  assert.equal(G.indoorPath([4, 4], [8, 0.2], room, []), null);
});
test("門口距相鄰牆 46.5 cm 可接入，44 cm 仍被擋住", () => {
  const room = rect(0, 0, 8, 8), path = G.indoorPath([4, 4], [8, 0.465], room, []);
  assert.ok(path); clearRoute(path.slice(0, -1), room);
  assert.equal(G.indoorPath([4, 4], [8, 0.44], room, []), null);
});
test("同一面牆的小角度分段不會誤擋門口（bedroomG 回歸）", () => {
  const room = [[0.0023, 0], [8, 0], [8, 8], [0, 8], [0, 3]], door = [-0.075, 3.04];
  const path = G.indoorPath([4, 4], door, room, []);
  assert.ok(path, "近平行牆段被誤當成另一面牆");
  assert.deepEqual(path.at(-1), door); clearRoute(path.slice(0, -1), room);
});
test("實際轉角不能套用近平行宿主牆容差", () => {
  const room = [[0, 0], [8, 0], [8, 8], [0, 8], [0, 3], [0.2, 0]];
  assert.equal(G.indoorPath([4, 4], [-0.075, 3.04], room, []), null);
});
test("通行空間的路線也內縮，不只處理來源居室", () => {
  const net = G.walkNetwork([], [{ element_id: "hall", boundary: L }],
    [{ position: [7.5, 0.5], room_ids: ["hall"] }], [{ entry_point: [0.5, 7.5] }], []);
  clearRoute(G.netPath(net, net.doorNode[0], net.stairNode[0]), L);
});
test("樓梯終點在牆面時保留原終點，僅最後短段接入", () => {
  const net = G.walkNetwork([], [{ element_id: "hall", boundary: L }],
    [{ position: [7.5, 0.5], room_ids: ["hall"] }], [{ entry_point: [0, 6] }], []);
  const path = G.netPath(net, net.doorNode[0], net.stairNode[0]);
  assert.deepEqual(path.at(-1), [0, 6]); assert.deepEqual(path.at(-2), [0.45, 6]); clearRoute(path.slice(0, -1), L);
});
test("兩空間透過門口連接，不可沿原始房間角點抄近路", () => {
  const spaces = [{ element_id: "a", boundary: rect(0, 0, 8, 8) }, { element_id: "b", boundary: rect(8.15, 0, 16, 8) }];
  const net = G.walkNetwork([], spaces, [{ position: [1, 1], room_ids: ["a"] }, { position: [8.075, 4], room_ids: ["a", "b"] }], [{ entry_point: [15, 7] }], []);
  const path = G.netPath(net, net.doorNode[0], net.stairNode[0]);
  assert.ok(path); assert.ok(path.some((p) => G.dist(p, [7.55, 4]) < 1e-6)); assert.ok(path.some((p) => G.dist(p, [8.6, 4]) < 1e-6));
});
test("中心線走廊未知或不足 0.9 m 淨寬時不可放行", () => {
  for (const width of [null, 0.8, 0.899, 0.9, 0.95, 1.2]) {
    const net = G.walkNetwork([{ centerline: [[0, 0], [10, 0]], clear_width_m: width }], [],
      [{ position: [0, 0], room_ids: [] }], [{ entry_point: [10, 0] }], []);
    assert.equal(Boolean(G.netPath(net, net.doorNode[0], net.stairNode[0])), width != null && width >= 0.9);
  }
});
test("中心線中段也不能切柱", () => {
  const net = G.walkNetwork([{ centerline: [[0, 0], [10, 0]], clear_width_m: 2 }], [],
    [{ position: [0, 0], room_ids: [] }], [{ entry_point: [10, 0] }], [rect(4, 0.2, 6, 0.4)]);
  assert.equal(G.netPath(net, net.doorNode[0], net.stairNode[0]), null);
});
test("遠離中心線的樓梯終點不可用未檢查的末段自動連接", () => {
  const net = G.walkNetwork([{ centerline: [[0, 0], [10, 0]], clear_width_m: 2 }], [],
    [{ position: [0, 0], room_ids: [] }], [{ entry_point: [20, 0] }], []);
  assert.equal(G.netPath(net, net.doorNode[0], net.stairNode[0]), null);
});
test("完整居室量測使用同一淨距路線，保留輸入且重算一致", () => {
  const room = { element_id: "r", boundary_closed: true, boundary: L, door_ids: ["d"], is_habitable: true };
  const snapshot = { rooms: [room, { element_id: "hall", is_circulation: true, boundary_closed: true, boundary: rect(-4, 0, 0, 8) }],
    doors: [{ element_id: "d", position: [0, 6], room_ids: ["r", "hall"] }],
    stairs: [{ element_id: "st", entry_point: [-3, 6], is_direct_stair: true }], corridors: [], obstacles: [] };
  const before = JSON.stringify(snapshot), facts = G.measureRoom(room, snapshot);
  assert.ok(!facts.missing, facts.missing); assert.equal(facts.route_clearance_m, 0.45);
  assert.equal(facts.geometry_version, "0.3.2-clearance"); near(facts.distance_exact_m, G.plen(facts.path));
  assert.deepEqual(facts.path.at(-1), [-3, 6]);
  assert.deepEqual(G.measureRoom(room, snapshot), facts); assert.equal(JSON.stringify(snapshot), before);
});
console.log(`${passed} geometry clearance tests passed`);
