// 產生範例資料：1 模型快照（A 外掛輸出）→ 2 專案設定（使用者手選）→ 3 檢討批次（引擎輸出）
// 全部是示意資料，跟專案1.rvt 無關
const fs = require("fs");
const path = require("path");
const engine = require("./engine/review.js");

const OUT = path.join(__dirname, "data");
const CL = 10.5; // 走廊中心線 y
const uid = (eid) => `5f2c1a7e-0b44-4c1d-9a3e-${eid.padStart(12, "0")}`;

// [ElementId, 房號, 名稱, x, y, 寬, 深, 門 x, 是否居室（null＝待確認）, 邊界是否封閉]
const roomRows = [
  ["523101", "301", "居室 301", 5, 0, 10, 9.5, 10, true, true],
  ["523102", "302", "居室 302", 15, 0, 10, 9.5, 20, true, true],
  ["523103", "303", "儲藏 303", 25, 0, 5, 9.5, 27.5, null, true],
  ["523104", "304", "居室 304", 35, 0, 12, 9.5, 41, true, true],
  ["523105", "305", "居室 305", 47, 0, 17, 9.5, 54, true, true],
  ["523106", "306", "居室 306", 0, 11.5, 14, 10.5, 7, true, false],
  ["523107", "307", "居室 307", 14, 11.5, 14, 10.5, 21, true, true],
  ["523108", "308", "居室 308", 28, 11.5, 14, 10.5, 35, true, true],
  ["523109", "309", "居室 309", 42, 11.5, 11, 10.5, 47, true, true],
  ["523110", "310", "居室 310", 53, 11.5, 11, 10.5, 56, true, true],
];

const rooms = [], doors = [];
roomRows.forEach(([eid, no, name, x, y, w, h, dx, hab, closed], i) => {
  const did = String(700101 + i);
  rooms.push({
    element_id: eid, unique_id: uid(eid), category: "Rooms", number: no, name, level: "3F",
    area_m2: w * h, boundary: [[x, y], [x + w, y], [x + w, y + h], [x, y + h]], boundary_closed: closed,
    door_ids: [did], is_habitable: hab,
  });
  doors.push({
    element_id: did, unique_id: uid(did), category: "Doors", room_ids: [eid, "corridor"],
    position: [dx, y === 0 ? y + h : y],
    clear_width_m: did === "700108" ? null : 0.9, clear_width_source: did === "700108" ? null : "類型參數「有效淨寬」",
  });
});

const snapshot = {
  schema: "egress-snapshot@0.2",
  project: {
    model_name: "專案1", model_guid: "示意-0000", revit_version: "2024", phase: "新建構造", design_option: "主模型",
    level: { name: "3F", elevation_m: 8.4 }, unit: "m", coordinate: "Revit 專案座標（Internal Origin）",
    extracted_at: "2026-10-19T20:00:00+08:00", note: "示意資料",
  },
  rooms, doors,
  stairs: [
    { element_id: "412207", unique_id: uid("412207"), category: "Stairs", name: "直通樓梯 S1", entry_point: [2.5, 9.5], endpoint_type: "first_step", is_direct_stair: true, footprint: [[0, 0], [5, 0], [5, 9.5], [0, 9.5]] },
    { element_id: "412318", unique_id: uid("412318"), category: "Stairs", name: "直通樓梯 S2", entry_point: [32.5, 9.5], endpoint_type: "fire_door", is_direct_stair: true, footprint: [[30, 0], [35, 0], [35, 9.5], [30, 9.5]] },
  ],
  corridors: [
    { element_id: "610021", unique_id: uid("610021"), name: "走廊 C1", centerline: [[0, CL], [42, CL]], clear_width_m: 2.0, clear_width_source: "量測：牆面至牆面", rooms_both_sides: true },
    { element_id: "610022", unique_id: uid("610022"), name: "走廊 C2", centerline: [[42, CL], [64, CL]], clear_width_m: 1.4, clear_width_source: "量測：牆面至設備櫃", rooms_both_sides: true },
  ],
  warnings: [
    { element_id: "523106", code: "ROOM_NOT_ENCLOSED", message: "居室 306：房間邊界未封閉（西側牆未設為房間邊界）" },
    { element_id: "700108", code: "DOOR_NO_CLEAR_WIDTH", message: "居室 308 的門：未標示有效淨寬" },
  ],
};

// 上面是照「y 向下」畫的草圖座標；資料一律用 Revit 座標（y 向上＝北），所以翻轉一次
const FLIP_H = 22;
const flip = (p) => [p[0], FLIP_H - p[1]];
for (const r of rooms) r.boundary = r.boundary.map(flip);
for (const d of doors) d.position = flip(d.position);
for (const s of snapshot.stairs) { s.entry_point = flip(s.entry_point); s.footprint = s.footprint.map(flip); }
for (const c of snapshot.corridors) c.centerline = c.centerline.map(flip);

const settings = {
  use_group: "B-3", use_group_set_by: "（範例）", floor_number: 3, below_ground: false,
  fire_resistant: true, tv_studio_with_audience: null, corridor_serves_classroom: null, duplex_housing: false,
};

const pack = JSON.parse(fs.readFileSync(path.join(__dirname, "regs", "rules.json"), "utf8"));
const batch = engine.review(snapshot, settings, pack, { created_at: "2026-10-19T20:05:00+08:00" });

fs.mkdirSync(OUT, { recursive: true });
const files = { "sample-1-模型快照.json": snapshot, "sample-2-專案設定.json": settings, "sample-3-檢討批次.json": batch };
for (const [name, data] of Object.entries(files)) fs.writeFileSync(path.join(OUT, name), JSON.stringify(data, null, 2), "utf8");
if (require.main === module) {
  for (const r of batch.rooms) console.log(r.name, r.status, r.facts ? r.facts.distance_m : "-", r.hints.map((h) => h.id).join(","));
  for (const c of batch.corridors) console.log(c.name, c.status, c.required_m);
}
module.exports = { snapshot, settings, pack };
