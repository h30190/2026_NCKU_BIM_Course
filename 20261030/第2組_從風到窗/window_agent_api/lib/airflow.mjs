// 室內風流試算(2D 位勢流):平面切成網格,窗口給風壓係數 Cp,外牆/服務核/柱不透風,解拉普拉斯方程得到流場。
// 只用來比較方案之間的差別(哪裡有風、哪裡是死角),不是 CFD,不考慮窗高、家具、渦流與熱浮力。
import { getModel, readJson } from './catalog.mjs';
import { deriveOfficeRoom } from '../engine_member3/data.mjs';

const CELL_M = 0.25; // 網格大小
const COVER_M_S = 0.2; // 「有感風速」門檻:≥ 0.2 m/s 算有通風
const DEAD_M_S = 0.05; // 死角門檻
const DIR8 = { N: 0, NE: 45, E: 90, SE: 135, S: 180, SW: 225, W: 270, NW: 315 };
const r = (x, n = 3) => Math.round(x * 10 ** n) / 10 ** n;

// 低層建築牆面平均風壓係數(經驗值):迎風 +0.8、側面 −0.6、背風 −0.5,中間用 cos 內插
// Cp(θ) = −0.225 + 0.65 cosθ + 0.375 cos2θ,θ = 風來向與牆外法線方位的夾角
export const cpOf = (deg) => { const t = (deg * Math.PI) / 180; return -0.225 + 0.65 * Math.cos(t) + 0.375 * Math.cos(2 * t); };

export function airflow(modelId, { proposals_file, proposal_id, wind_from_deg }) {
  const m = getModel(modelId);
  const doc = readJson(proposals_file);
  const p = doc.proposals.find((x) => x.proposal_id === proposal_id);
  if (!p) throw new Error(`${proposals_file} 沒有方案 ${proposal_id}`);
  const prevailing = doc.weather?.prevailing?.[0];
  const windFrom = wind_from_deg != null ? Number(wind_from_deg) : DIR8[prevailing];
  if (windFrom == null || Number.isNaN(windFrom)) throw new Error('方案檔沒有主風向,請指定 wind_from_deg');
  const U = doc.weather?.mean_speed_m_s;
  if (!U) throw new Error('方案檔沒有平均風速');

  // 室內範圍:外牆中心線外框內縮半個牆厚(兩個模型都是矩形外牆)
  const walls = new Map(m.building.walls.map((w) => [w.wall_id, w]));
  const ext = m.building.walls.filter((w) => w.is_exterior);
  const pts = ext.flatMap((w) => [w.location_line.start_xyz_m, w.location_line.end_xyz_m]);
  const t2 = Math.max(...ext.map((w) => w.thickness_m)) / 2;
  const minX = Math.min(...pts.map((q) => q[0])) + t2, maxX = Math.max(...pts.map((q) => q[0])) - t2;
  const minY = Math.min(...pts.map((q) => q[1])) + t2, maxY = Math.max(...pts.map((q) => q[1])) - t2;
  const nx = Math.round((maxX - minX) / CELL_M), ny = Math.round((maxY - minY) / CELL_M);
  const dx = (maxX - minX) / nx, dy = (maxY - minY) / ny;
  const at = (i, j) => j * nx + i;
  const cx = (i) => minX + (i + 0.5) * dx, cy = (j) => minY + (j + 0.5) * dy;

  // 不透風的格子:服務核外輪廓、柱
  const solid = new Uint8Array(nx * ny);
  const block = (b) => { for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++) if (cx(i) >= b.minX && cx(i) <= b.maxX && cy(j) >= b.minY && cy(j) <= b.maxY) solid[at(i, j)] = 1; };
  const cores = deriveOfficeRoom(m.building).cores.map((c) => c.box);
  cores.forEach(block);
  const columns = (m.building.columns || []).filter((c) => c.BoundingBox?.HasBoundingBox).map((c) => ({ minX: c.BoundingBox.MinX / 1000, minY: c.BoundingBox.MinY / 1000, maxX: c.BoundingBox.MaxX / 1000, maxY: c.BoundingBox.MaxY / 1000 }));
  columns.forEach(block);

  // 窗口:落在四個邊上,記錄各邊每一格的 Cp(NaN = 牆,不透風)
  const edge = { top: new Float64Array(nx).fill(NaN), bottom: new Float64Array(nx).fill(NaN), left: new Float64Array(ny).fill(NaN), right: new Float64Array(ny).fill(NaN) };
  const edgeWin = { top: new Int16Array(nx).fill(-1), bottom: new Int16Array(nx).fill(-1), left: new Int16Array(ny).fill(-1), right: new Int16Array(ny).fill(-1) };
  const wins = p.windows.map((w, k) => {
    const wall = walls.get(w.wall_id);
    const n = wall.orientation.outward_normal_xyz;
    const side = n[1] > 0.9 ? 'top' : n[1] < -0.9 ? 'bottom' : n[0] > 0.9 ? 'right' : n[0] < -0.9 ? 'left' : null;
    if (!side) throw new Error(`牆 ${w.wall_id} 不是正南北/東西向,目前的試算只支援矩形平面`);
    const x = w.position_mm.x / 1000, y = w.position_mm.y / 1000, h = w.width_m / 2;
    const theta = (((windFrom - wall.orientation.azimuth_deg) % 360) + 540) % 360 - 180;
    const cp = cpOf(theta);
    const horiz = side === 'top' || side === 'bottom';
    const len = horiz ? nx : ny;
    for (let s = 0; s < len; s++) if (Math.abs((horiz ? cx(s) : cy(s)) - (horiz ? x : y)) <= h) { edge[side][s] = cp; edgeWin[side][s] = k; }
    return { k, wall_id: w.wall_id, orientation: w.orientation, side, x_m: x, y_m: y, width_m: w.width_m, cp: r(cp), cd: w.cd ?? 0.6 };
  });

  // 鄰格:回傳 [值的權重, 值×權重];牆/障礙物 = 不透風(不計入),窗口 = Dirichlet(鬼格法,權重 2)
  const phi = new Float64Array(nx * ny).fill(wins.reduce((s, w) => s + w.cp, 0) / wins.length);
  const nb = (i, j, di, dj) => {
    const ii = i + di, jj = j + dj;
    if (ii < 0) return Number.isNaN(edge.left[j]) ? null : ['b', edge.left[j]];
    if (ii >= nx) return Number.isNaN(edge.right[j]) ? null : ['b', edge.right[j]];
    if (jj < 0) return Number.isNaN(edge.bottom[i]) ? null : ['b', edge.bottom[i]];
    if (jj >= ny) return Number.isNaN(edge.top[i]) ? null : ['b', edge.top[i]];
    return solid[at(ii, jj)] ? null : ['f', at(ii, jj)];
  };
  const DIRS = [[1, 0], [-1, 0], [0, 1], [0, -1]];
  const NB = Array.from({ length: nx * ny }, (_, c) => (solid[c] ? null : DIRS.map(([a, b]) => nb(c % nx, Math.floor(c / nx), a, b)).filter(Boolean)));

  // SOR 疊代
  const range = Math.max(...wins.map((w) => w.cp)) - Math.min(...wins.map((w) => w.cp));
  let iters = 0;
  if (range > 1e-6) {
    for (; iters < 20000; iters++) {
      let maxd = 0;
      for (let c = 0; c < nx * ny; c++) {
        const L = NB[c];
        if (!L || !L.length) continue;
        let sum = 0, cnt = 0;
        for (const [kind, v] of L) { if (kind === 'f') { sum += phi[v]; cnt++; } else { sum += 2 * v; cnt += 2; } }
        const nv = phi[c] + 1.9 * (sum / cnt - phi[c]);
        maxd = Math.max(maxd, Math.abs(nv - phi[c]));
        phi[c] = nv;
      }
      if (maxd < range * 1e-7) break;
    }
  }

  // 速度 = −∇φ(由高風壓流向低風壓)
  const val = (i, j, di, dj) => { const q = nb(i, j, di, dj); return q == null ? phi[at(i, j)] : q[0] === 'f' ? phi[q[1]] : 2 * q[1] - phi[at(i, j)]; };
  const gx = new Float64Array(nx * ny), gy = new Float64Array(nx * ny);
  for (let j = 0; j < ny; j++) for (let i = 0; i < nx; i++) {
    const c = at(i, j);
    if (solid[c]) continue;
    gx[c] = -(val(i, j, 1, 0) - val(i, j, -1, 0)) / (2 * dx);
    gy[c] = -(val(i, j, 0, 1) - val(i, j, 0, -1)) / (2 * dy);
  }

  // 每扇窗的流入速度(正 = 進風、負 = 出風),在窗口面上取梯度
  const face = { top: (s) => [s, ny - 1, dy], bottom: (s) => [s, 0, dy], left: (s) => [0, s, dx], right: (s) => [nx - 1, s, dx] };
  const winFlow = wins.map(() => []);
  for (const side of Object.keys(edge)) edgeWin[side].forEach((k, s) => {
    if (k < 0) return;
    const [i, j, d] = face[side](s);
    if (solid[at(i, j)]) return;
    winFlow[k].push((edge[side][s] - phi[at(i, j)]) / (d / 2));
  });

  // 速度尺度:進風口平均風速 = Cd × 室外平均風速 × √(Cp 最大差),其餘格子等比例(孔口公式的簡化)
  const inflow = winFlow.flat().filter((v) => v > 0);
  const meanIn = inflow.length ? inflow.reduce((s, v) => s + v, 0) / inflow.length : 0;
  const cd = wins[0]?.cd ?? 0.6;
  const target = cd * U * Math.sqrt(Math.max(range, 0));
  const k = meanIn > 0 ? target / meanIn : 0;

  const speed = Array.from({ length: nx * ny }, (_, c) => (solid[c] ? null : Math.hypot(gx[c], gy[c]) * k));
  const free = speed.filter((v) => v != null);
  const share = (f) => r(free.filter(f).length / free.length, 4);

  return {
    model_id: m.model_id,
    proposal_id,
    wind: { from_deg: windFrom, from: prevailing ?? null, mean_speed_m_s: U, source: wind_from_deg != null ? '指定' : '方案檔的主風向' },
    grid: { nx, ny, minX, minY, dx, dy, iterations: iters },
    vx: Array.from(gx, (v, c) => (solid[c] ? null : r(v * k, 4))),
    vy: Array.from(gy, (v, c) => (solid[c] ? null : r(v * k, 4))),
    cores, columns,
    windows: wins.map((w, i) => {
      const f = winFlow[i];
      const v = f.length ? (f.reduce((s, x) => s + x, 0) / f.length) * k : 0;
      return { ...w, role: v >= DEAD_M_S ? 'inlet' : v <= -DEAD_M_S ? 'outlet' : 'neutral', speed_m_s: r(Math.abs(v), 3) };
    }),
    stats: {
      coverage: share((v) => v >= COVER_M_S),
      coverage_threshold_m_s: COVER_M_S,
      dead_zone: share((v) => v < DEAD_M_S),
      dead_zone_threshold_m_s: DEAD_M_S,
      mean_speed_m_s: r(free.reduce((s, v) => s + v, 0) / free.length, 3),
      bins: [[0, 0.05], [0.05, 0.2], [0.2, 0.5], [0.5, Infinity]].map(([a, b]) => ({ range: b === Infinity ? `≥${a}` : `${a}–${b}`, share: share((v) => v >= a && v < b) })),
    },
    assumptions: [
      '2D 平面位勢流:不考慮窗高、樑、家具隔間、渦流與熱浮力,只用來比較方案',
      `風壓係數 Cp:迎風 +0.8、側面 −0.6、背風 −0.5(低層建築經驗值),主風向 ${prevailing ?? windFrom + '°'}`,
      `速度尺度:進風口平均風速 = Cd ${cd} × 室外平均風速 ${U} m/s × √ΔCp,室外風速取氣象站值未做高度修正`,
      `通風覆蓋率:室內(扣除服務核與柱)風速 ≥ ${COVER_M_S} m/s 的面積比例;網格 ${CELL_M} m`,
    ],
  };
}
