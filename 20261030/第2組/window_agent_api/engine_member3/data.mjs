// 載入組員 1、2 的 JSON 與任意 EPW,並推導 Agent 需要的衍生資料(辦公區面積、服務核範圍、窗型目錄)。
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { loadWindFromEpw } from './epw.mjs';

const here = path.dirname(fileURLToPath(import.meta.url));
export const ROOT = path.resolve(here, '..');

const readJson = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));
export const round = (x, n = 3) => Math.round(x * 10 ** n) / 10 ** n;

// 團隊決議(2026-10-22):窗台 0.9 m;Double-Hung 開啟比例先設 0.5;不使用風險分數;通風預設以全年氣象為準。
export const DEFAULTS = {
  sill_height_m: 0.9,
  double_hung_opening_ratio: 0.5,
  frame_clearance_per_side_m: 0.05, // 粗開口比名義寬度大的篩選假設
  min_gap_between_windows_m: 0.3, // 篩選假設
  head_margin_m: 0.4, // 窗頂距牆頂的篩選假設(樑、樓板)
  max_wwr_per_wall: 0.4, // 單面外牆窗牆比上限(篩選假設,組員 2 尚未提供)
  vp_target: 0.1, // EEWH VP >= 0.10 得 100 分,達到後不再加分
  legal_ratio: 0.05, // 第43條 5%
  opposing_dot: -0.7, // 兩牆外法線內積 ≤ 此值視為相對牆(約 ±45° 內),可形成對流
  weights: { orientation: 0.5, cross_flow_vp: 0.4, away_from_core: 0.1 }, // 排序權重(示意值)
};

// 找 EPW:使用者指定 > 專案內唯一的 .epw。找到多個時不猜,直接報錯,避免默默用錯地點。
export function findEpw(explicit) {
  if (explicit) {
    const p = path.resolve(explicit);
    if (!fs.existsSync(p)) throw new Error(`找不到 EPW 檔:${p}`);
    return p;
  }
  const found = [];
  const stack = [ROOT];
  while (stack.length) {
    const d = stack.pop();
    for (const e of fs.readdirSync(d, { withFileTypes: true })) {
      if (e.name === 'node_modules' || e.name === '.git') continue;
      const p = path.join(d, e.name);
      if (e.isDirectory()) stack.push(p);
      else if (/\.epw$/i.test(e.name)) found.push(p);
    }
  }
  if (found.length === 1) return found[0];
  if (!found.length) throw new Error('找不到 EPW 檔,請以 --epw 指定(下載方式見「下載EPW教學.md」)');
  throw new Error(`專案內有 ${found.length} 個 EPW,請以 --epw 指定要用哪一個:\n${found.join('\n')}`);
}

// comfort: 省略 = 預設開窗時段篩選(見 epw.mjs DEFAULT_COMFORT);false = 不篩選;或自訂 {t_min_c,t_max_c,hour_from,hour_to}
export function loadInputs({ model = '01', epw = null, months = null, comfort } = {}) {
  const dir = path.join(ROOT, `BIM_商辦標準層${model}_JSON資料`);
  const epwFile = findEpw(epw);
  return {
    model,
    building: readJson(path.join(dir, 'building_data.json')),
    families: readJson(path.join(dir, 'window_families.json')),
    windowTypes: readJson(path.join(ROOT, 'window_types.json')),
    rules: readJson(path.join(ROOT, 'design_rules_wind.json')),
    epwFile,
    wind: loadWindFromEpw(epwFile, months, comfort === false ? null : comfort),
  };
}

// ---------- 幾何 ----------
const near = (a, b, tol) => Math.hypot(a[0] - b[0], a[1] - b[1]) <= tol;

// 依端點相接把牆串成封閉環,回傳依序的起點;串不成則回傳 null
function chainLoop(walls) {
  const segs = walls.map((w) => [w.location_line.start_xyz_m.slice(0, 2), w.location_line.end_xyz_m.slice(0, 2)]);
  const tol = 0.05;
  const pts = [segs[0][0]];
  let cur = segs[0][1];
  const used = new Set([0]);
  while (used.size < segs.length) {
    const i = segs.findIndex((s, k) => !used.has(k) && (near(s[0], cur, tol) || near(s[1], cur, tol)));
    if (i < 0) return null;
    used.add(i);
    pts.push(cur);
    cur = near(segs[i][0], cur, tol) ? segs[i][1] : segs[i][0];
  }
  return near(cur, pts[0], tol) ? pts : null;
}

// 直角多邊形中心線 → 內縮(或外擴)半個牆厚後的面積:A ∓ P·t/2 ± Σ(凸/凹角)·t²/4
function offsetArea(pts, t, outward) {
  let a2 = 0;
  let perim = 0;
  const n = pts.length;
  for (let i = 0; i < n; i++) {
    const p = pts[i];
    const q = pts[(i + 1) % n];
    a2 += p[0] * q[1] - q[0] * p[1];
    perim += Math.hypot(q[0] - p[0], q[1] - p[1]);
  }
  const sgn = Math.sign(a2);
  let corner = 0;
  for (let i = 0; i < n; i++) {
    const p = pts[(i + n - 1) % n];
    const q = pts[i];
    const r = pts[(i + 1) % n];
    const cross = (q[0] - p[0]) * (r[1] - q[1]) - (q[1] - p[1]) * (r[0] - q[0]);
    corner += Math.sign(cross) === sgn ? 1 : -1;
  }
  const area = Math.abs(a2) / 2;
  const s = outward ? 1 : -1;
  return area + s * (perim * t) / 2 + (corner * t * t) / 4;
}

const bboxOf = (walls) => {
  const pts = walls.flatMap((w) => [w.location_line.start_xyz_m, w.location_line.end_xyz_m]);
  return {
    minX: Math.min(...pts.map((p) => p[0])), maxX: Math.max(...pts.map((p) => p[0])),
    minY: Math.min(...pts.map((p) => p[1])), maxY: Math.max(...pts.map((p) => p[1])),
  };
};

// 內牆依端點相接分群 → 每群一個服務核(外包矩形)
export function deriveCores(building) {
  const inner = building.walls.filter((w) => !w.is_exterior);
  const parent = inner.map((_, i) => i);
  const find = (i) => (parent[i] === i ? i : (parent[i] = find(parent[i])));
  const ends = inner.map((w) => [w.location_line.start_xyz_m, w.location_line.end_xyz_m]);
  for (let i = 0; i < inner.length; i++) {
    for (let j = i + 1; j < inner.length; j++) {
      if (ends[i].some((p) => ends[j].some((q) => near(p, q, 0.3)))) parent[find(i)] = find(j);
    }
  }
  const groups = {};
  inner.forEach((w, i) => (groups[find(i)] ||= []).push(w));
  return Object.values(groups).map((ws, k) => {
    const b = bboxOf(ws);
    const t = ws[0].thickness_m;
    const loop = chainLoop(ws);
    return {
      core_id: `core_${k + 1}`,
      wall_ids: ws.map((w) => w.wall_id),
      box: { minX: b.minX - t / 2, maxX: b.maxX + t / 2, minY: b.minY - t / 2, maxY: b.maxY + t / 2 },
      footprint_m2: loop ? offsetArea(loop, t, true) : (b.maxX - b.minX + t) * (b.maxY - b.minY + t),
      inner_area_m2: loop ? offsetArea(loop, t, false) : null,
    };
  });
}

// 辦公區面積:優先用 Revit 中非服務核的 Room;沒有時由外牆環內縮後減去各服務核外輪廓。
export function deriveOfficeRoom(building) {
  const cores = deriveCores(building);
  const officeRooms = (building.rooms || []).filter((r) => !/core|service|服務|核|shaft|stair|elev/i.test(`${r.name} ${r.number}`) && r.area_m2 > 0);
  if (officeRooms.length) {
    return {
      room_id: officeRooms.map((r) => r.room_id).join('+'), use_type: 'office',
      floor_area_m2: round(officeRooms.reduce((s, r) => s + r.area_m2, 0), 3),
      basis: 'Revit Room 元件面積', cores, assumption_flag: false,
    };
  }
  const ext = building.walls.filter((w) => w.is_exterior);
  const t = ext[0].thickness_m;
  const loop = chainLoop(ext);
  let gross;
  let basis;
  if (loop) {
    gross = offsetArea(loop, t, false);
    basis = '外牆中心線封閉環內縮半個牆厚的淨面積 − 各服務核外輪廓面積(牆幾何推算,非 Revit Room)';
  } else {
    const b = bboxOf(ext);
    gross = (b.maxX - b.minX - t) * (b.maxY - b.minY - t);
    basis = '外牆無法串成封閉環,改用外包矩形淨面積 − 各服務核外輪廓面積(粗估)';
  }
  const coreRoom = (building.rooms || []).find((r) => /core|服務核/i.test(r.name));
  const partitionM2 = building.walls.filter((w) => !w.is_exterior).reduce((s, w) => s + w.length_m * w.thickness_m, 0);
  // 有 Revit Core Room 時:扣除 Room 面積 + 內牆本身面積(精確,且服務核貼著外牆時也成立);否則用內牆外包矩形粗估
  const coreSum = coreRoom?.area_m2 ? coreRoom.area_m2 + partitionM2 : cores.reduce((s, c) => s + c.footprint_m2, 0);
  if (coreRoom?.area_m2) basis += ';服務核面積取 Revit Core Room + 內牆面積';
  return {
    room_id: 'office_derived', use_type: 'office',
    floor_area_m2: round(gross - coreSum, 3), gross_inner_area_m2: round(gross, 3), core_footprint_m2: round(coreSum, 3),
    basis, cores,
    core_room_check: coreRoom && cores[0] ? { revit_room_area_m2: coreRoom.area_m2, derived_inner_area_m2: round(cores[0].inner_area_m2, 3) } : null,
    assumption_flag: true,
  };
}

// 窗型目錄:僅取模型已載入、可通風的 family 的完整 type。
export function buildCatalog({ families, windowTypes }, opts = DEFAULTS) {
  const wt = (id) => windowTypes.window_types.find((x) => x.window_type === id);
  const casement = wt('side_hung_casement');
  const sliding = wt('horizontal_sliding');
  const mapping = {
    'M_Window-Casement-Double': {
      window_type: 'side_hung_casement',
      opening_ratio: Math.sin((casement.geometry.defaults.open_angle * Math.PI) / 180),
      cd: casement.airflow_discharge_coefficient_cd.default_value,
      basis: `window_types.json:側開窗,開啟角 ${casement.geometry.defaults.open_angle}°`,
    },
    'M_Window-Double-Hung': {
      window_type: 'double_hung',
      opening_ratio: opts.double_hung_opening_ratio,
      cd: sliding.airflow_discharge_coefficient_cd.default_value, // window_types.json 尚無上下推拉窗型,Cd 暫借橫拉窗
      basis: '團隊決議:開啟比例暫設 0.5;Cd 暫借橫拉窗 0.60(待組員 2 補窗型)',
    },
  };
  const catalog = [];
  for (const fam of families.families) {
    const m = mapping[fam.family_name];
    if (!m || !fam.supports_ventilation_by_family_name) continue;
    for (const ty of fam.types) {
      catalog.push({
        family_name: fam.family_name, type_name: ty.type_name, mcp_windowType: ty.mcp_windowType, type_id: ty.type_id,
        width_m: ty.nominal_width_mm / 1000, height_m: ty.nominal_height_mm / 1000, ...m,
      });
    }
  }
  return catalog;
}
