// 開窗方案生成:依外牆方位/長度/高度、服務核位置與任意地點的風資料,枚舉位置與窗型 → 規則檢核 → 排序。
import { DEFAULTS, buildCatalog, deriveOfficeRoom, round } from './data.mjs';
import { compass8 } from './epw.mjs';

const AIR_DENSITY = 1.2;
const SCORE_TOLERANCE = 0.03; // 示意值

const normalOf = (wall) => {
  const n = wall.orientation.outward_normal_xyz;
  if (n) return [n[0], n[1]];
  const a = (wall.orientation.azimuth_deg * Math.PI) / 180; // 方位:北=0、順時針
  return [Math.sin(a), Math.cos(a)];
};
const dot = (a, b) => a[0] * b[0] + a[1] * b[1];

const occupiedWidth = (type, o) => type.width_m + 2 * o.frame_clearance_per_side_m;
const intervalCapacity = (len, w, o) => Math.max(0, Math.floor((len - o.min_gap_between_windows_m) / (w + o.min_gap_between_windows_m)));

// 服務核投影到該牆 s 軸上的範圍:牆面正對服務核的區段(開窗後視線與氣流會被核心牆擋住)
function coreShadows(wall, cores) {
  const st = wall.location_line.start_xyz_m;
  const u = wall.location_line.s_axis_unit_xyz;
  const n = normalOf(wall);
  return cores
    .map((c) => {
      const b = c.box;
      const corners = [[b.minX, b.minY], [b.minX, b.maxY], [b.maxX, b.minY], [b.maxX, b.maxY]];
      const inFront = corners.every((p) => dot([p[0] - st[0], p[1] - st[1]], n) < 0); // 核心在牆內側
      if (!inFront) return null;
      const ss = corners.map((p) => (p[0] - st[0]) * u[0] + (p[1] - st[1]) * u[1]);
      return [Math.min(...ss), Math.max(...ss)];
    })
    .filter(Boolean);
}

// 把可排窗區間依服務核陰影切成「避開服務核」與「正對服務核」兩類子區間
function splitIntervals(wall, cores) {
  const shadows = coreShadows(wall, cores);
  const out = [];
  for (const iv of wall.available_window_intervals) {
    if (iv.office_candidate === false) continue; // 組員 1 標示屬於服務核/非辦公房間的牆段,不開窗
    let cuts = [[iv.s_start_m, iv.s_end_m, true]];
    for (const [lo, hi] of shadows) {
      cuts = cuts.flatMap(([a, b, pref]) => {
        if (!pref || hi <= a || lo >= b) return [[a, b, pref]];
        const parts = [];
        if (lo > a) parts.push([a, lo, true]);
        parts.push([Math.max(a, lo), Math.min(b, hi), false]);
        if (hi < b) parts.push([hi, b, true]);
        return parts;
      });
    }
    for (const [a, b, pref] of cuts) if (b - a > 1e-6) out.push({ s_start_m: a, s_end_m: b, length_m: b - a, preferred: pref });
  }
  return out;
}

// 在單面外牆放 k 扇同型窗:先填滿避開服務核的區段,再填正對服務核的區段;區間內等間距。放不下回傳 null。
function placeOnWall(wall, type, k, cores, o) {
  const w = occupiedWidth(type, o);
  const ivs = splitIntervals(wall, cores).map((iv) => ({ ...iv, cap: intervalCapacity(iv.length_m, w, o), n: 0 }));
  for (let i = 0; i < k; i++) {
    const open = ivs.filter((iv) => iv.n < iv.cap);
    if (!open.length) return null;
    open.sort((a, b) => Number(b.preferred) - Number(a.preferred) || b.length_m / (b.n + 1) - a.length_m / (a.n + 1));
    open[0].n++;
  }
  const loc = wall.location_line;
  const out = [];
  for (const iv of ivs) {
    if (!iv.n) continue;
    const gap = (iv.length_m - iv.n * w) / (iv.n + 1);
    for (let i = 0; i < iv.n; i++) {
      const s = iv.s_start_m + gap * (i + 1) + w * (i + 0.5);
      const x = loc.start_xyz_m[0] + s * loc.s_axis_unit_xyz[0];
      const y = loc.start_xyz_m[1] + s * loc.s_axis_unit_xyz[1];
      out.push({
        wall_id: wall.wall_id,
        orientation: compass8(wall.orientation.azimuth_deg),
        facing_orientation_deg: wall.orientation.azimuth_deg,
        window_type: type.window_type, family_name: type.family_name, type_name: type.type_name, mcp_windowType: type.mcp_windowType,
        width_m: type.width_m, height_m: type.height_m, occupied_width_m: round(w, 3),
        s_center_m: round(s, 3), sill_height_m: o.sill_height_m,
        position_mm: { x: Math.round(x * 1000), y: Math.round(y * 1000) },
        facing_core: !iv.preferred,
        plan_xy_m: [x, y],
        opening_ratio: round(type.opening_ratio, 4), cd: type.cd,
        gross_area_m2: round(type.width_m * type.height_m, 4),
        legal_effective_open_area_m2: round(type.width_m * type.height_m * type.opening_ratio, 4),
        airflow_effective_area_m2: round(type.width_m * type.height_m * type.opening_ratio, 4),
      });
    }
  }
  return out;
}

// 平面線段是否穿過矩形(Liang–Barsky)
function segmentHitsBox(p, q, b) {
  let t0 = 0;
  let t1 = 1;
  const dx = q[0] - p[0];
  const dy = q[1] - p[1];
  for (const [pp, qq] of [[-dx, p[0] - b.minX], [dx, b.maxX - p[0]], [-dy, p[1] - b.minY], [dy, b.maxY - p[1]]]) {
    if (pp === 0) {
      if (qq < 0) return false;
    } else {
      const r = qq / pp;
      if (pp < 0) {
        if (r > t1) return false;
        if (r > t0) t0 = r;
      } else {
        if (r < t0) return false;
        if (r < t1) t1 = r;
      }
    }
  }
  return true;
}

function evaluate(windows, ctx) {
  const { room, wind, wallInfo, pairs, o } = ctx;
  const legal = windows.reduce((s, w) => s + w.legal_effective_open_area_m2, 0);
  const required = o.legal_ratio * room.floor_area_m2;
  const opp = (a, b) => dot(wallInfo[a].n, wallInfo[b].n) <= o.opposing_dot;

  // EEWH 對流窗:存在相對外牆的窗,且兩窗中心連線不穿過任何服務核
  let acj = 0;
  let avi = 0;
  for (const w of windows) {
    const cross = windows.some((x) => x.wall_id !== w.wall_id && opp(w.wall_id, x.wall_id) && !room.cores.some((c) => segmentHitsBox(w.plan_xy_m, x.plan_xy_m, c.box)));
    w.cross_flow = cross;
    if (cross) acj += w.legal_effective_open_area_m2;
    else avi += w.legal_effective_open_area_m2;
  }
  const vp = (avi + 3 * acj) / room.floor_area_m2;
  const vpScore = vp >= 0.1 ? 100 : vp >= 0.07 ? 80 : vp >= 0.05 ? 60 : vp >= 0.03 ? 40 : 10;

  const byWall = {};
  for (const w of windows) {
    const a = (byWall[w.wall_id] ||= { wall_id: w.wall_id, orientation: w.orientation, n: 0, gross: 0, air: 0, cd_air: 0 });
    a.n++;
    a.gross += w.gross_area_m2;
    a.air += w.airflow_effective_area_m2;
    a.cd_air += w.cd * w.airflow_effective_area_m2;
  }
  let wwrMax = 0;
  for (const a of Object.values(byWall)) {
    a.wwr = a.gross / wallInfo[a.wall_id].area_m2;
    wwrMax = Math.max(wwrMax, a.wwr);
  }

  // 簡化風量:Q = 3600·Cd·Aeq·v,v = 平均風速 × 迎風面方向分數(PoC 排序用,非法規值)
  let q = 0;
  for (const [a, b] of pairs) {
    if (!byWall[a] || !byWall[b]) continue;
    const aeq = 1 / Math.sqrt(1 / byWall[a].air ** 2 + 1 / byWall[b].air ** 2);
    const cd = (byWall[a].cd_air + byWall[b].cd_air) / (byWall[a].air + byWall[b].air);
    const s = Math.max(wallInfo[a].score, wallInfo[b].score);
    q += 3600 * cd * aeq * Math.sqrt((2 * 0.5 * AIR_DENSITY * (wind.mean_speed_m_s * s) ** 2) / AIR_DENSITY);
  }

  // 方向分數:穿堂風的進風側決定效果,出風側的窗沿用相對牆(進風側)的分數
  const effScore = (id) => Math.max(wallInfo[id].score, ...Object.keys(byWall).filter((j) => j !== id && opp(id, j)).map((j) => wallInfo[j].score));
  const orient = windows.reduce((s, w) => s + w.legal_effective_open_area_m2 * effScore(w.wall_id), 0) / legal;
  const facing = windows.filter((w) => w.facing_core).length;
  const awayCore = 1 - facing / windows.length;
  const W = o.weights;
  const score = W.orientation * orient + W.cross_flow_vp * Math.min(1, vp / o.vp_target) + W.away_from_core * awayCore;
  return { legal, required, legalPass: legal >= required, vp, vpScore, vpTarget: o.vp_target, acj, avi, byWall, wwrMax, wwrPass: wwrMax <= o.max_wwr_per_wall, q, orient, awayCore, facing, score };
}

// 方案配置:每一組相對外牆各成一案;全部外牆合併一案;若完全沒有相對牆則每面外牆單獨成案。
function buildSchemes(exterior, wallInfo, o) {
  const label = (ids) => ids.map((id) => wallInfo[id].label).join('-');
  const pairs = [];
  for (let i = 0; i < exterior.length; i++) {
    for (let j = i + 1; j < exterior.length; j++) {
      const a = exterior[i].wall_id;
      const b = exterior[j].wall_id;
      if (dot(wallInfo[a].n, wallInfo[b].n) <= o.opposing_dot) pairs.push([a, b]);
    }
  }
  const schemes = pairs.map(([a, b]) => ({ name: label([a, b]), wall_ids: [a, b] }));
  if (exterior.length > 2 && pairs.length) schemes.push({ name: label(exterior.map((w) => w.wall_id)), wall_ids: exterior.map((w) => w.wall_id) });
  if (!pairs.length) exterior.forEach((w) => schemes.push({ name: `單側 ${wallInfo[w.wall_id].label}`, wall_ids: [w.wall_id] }));
  return { schemes, pairs };
}

// 共用前處理:辦公區、窗型目錄、外牆與迎風分數、方案配置(generateProposals / customProposal / wallCapacities 共用)
function prepare(inputs, params = {}) {
  const o = { ...DEFAULTS, ...(params.options || {}) };
  const room = deriveOfficeRoom(inputs.building);
  const wind = inputs.wind;
  const catalog = buildCatalog(inputs, o);
  const exterior = inputs.building.walls.filter((w) => w.is_exterior && w.available_window_intervals?.length);
  if (!exterior.length) throw new Error('building_data 沒有含 available_window_intervals 的外牆');

  const wallInfo = Object.fromEntries(
    exterior.map((w) => [w.wall_id, { n: normalOf(w), score: wind ? wind.score(w.orientation.azimuth_deg) : null, label: compass8(w.orientation.azimuth_deg), area_m2: w.length_m * w.height_m, wall: w }]),
  );
  const { schemes, pairs } = buildSchemes(exterior, wallInfo, o);
  return { o, room, wind, catalog, exterior, wallInfo, schemes, pairs, ctx: { room, wind, wallInfo, pairs, o } };
}

const typeLabel = (t) => `${t.family_name}: ${t.type_name}`;
const fitsHeight = (t, wall, o) => o.sill_height_m + t.height_m <= wall.height_m - o.head_margin_m;
// 加窗過程的一步(給「為什麼是這個窗型/窗數」說明用)
const stepOf = (groups, ev) => ({
  total: groups.reduce((s, g) => s + g.length, 0),
  per_wall: groups.map((g) => ({ wall_id: g[0].wall_id, orientation: g[0].orientation, n: g.length })),
  legal_m2: round(ev.legal, 3), required_m2: round(ev.required, 3), legal_pass: ev.legalPass,
  vp: round(ev.vp, 4), vp_pass: ev.vp >= ev.vpTarget,
  wwr_max: round(ev.wwrMax, 4), wwr_pass: ev.wwrPass,
});

export function generateProposals(inputs, params = {}) {
  const { o, room, wind, catalog, wallInfo, schemes, ctx } = prepare(inputs, params);
  const meanLen = (ids) => ids.reduce((s, id) => s + wallInfo[id].wall.length_m, 0) / ids.length;

  const best = [];
  const rejected = [];
  const extras = []; // 各配置未被選為代表的候選,配置不足 3 個時用來補滿
  for (const scheme of schemes) {
    const walls = scheme.wall_ids.map((id) => wallInfo[id].wall);
    const ml = meanLen(scheme.wall_ids);
    const cands = [];
    const typeTrace = []; // 這個配置每個窗型的試算結果
    for (const type of catalog) {
      if (!fitsHeight(type, walls[0], o)) {
        typeTrace.push({ type: typeLabel(type), width_m: type.width_m, height_m: type.height_m, outcome: 'rejected', stop: 'too_tall', steps: [] });
        continue;
      }
      let accepted = null;
      let fallback = null;
      let prevTotal = -1;
      let stop = 'k_limit';
      const steps = [];
      for (let k = 1; k <= 40; k++) {
        // 各牆窗數與牆長成正比,放不下就停在該牆容量上限
        const groups = walls.map((w) => {
          for (let kk = Math.ceil((k * w.length_m) / ml); kk >= 1; kk--) {
            const g = placeOnWall(w, type, kk, room.cores, o);
            if (g) return g;
          }
          return null;
        });
        if (groups.some((g) => !g)) { stop = 'no_space'; break; }
        const windows = groups.flat();
        if (windows.length === prevTotal) { stop = 'capacity'; break; } // 已達容量上限
        prevTotal = windows.length;
        const ev = evaluate(windows, ctx);
        steps.push(stepOf(groups, ev));
        if (!ev.wwrPass) { stop = 'wwr'; break; }
        if (!ev.legalPass) continue;
        fallback ||= { windows, ev, steps: steps.slice() };
        if (ev.vp >= o.vp_target) {
          accepted = { windows, ev, steps: steps.slice() };
          stop = 'target_met';
          break;
        }
      }
      const pick = accepted || fallback;
      typeTrace.push({ type: typeLabel(type), width_m: type.width_m, height_m: type.height_m, outcome: pick ? 'candidate' : 'rejected', stop, window_count: pick?.windows.length ?? null, score: pick ? round(pick.ev.score, 4) : null, steps });
      if (!pick) {
        rejected.push({ scheme: scheme.name, type: typeLabel(type), reason: '放不下足夠窗數、窗牆比超限,或無法達到 5% 法定有效通風面積' });
        continue;
      }
      cands.push({ scheme, type, ...pick, typeTrace });
    }
    if (cands.length) {
      // 分數相差不到 SCORE_TOLERANCE 視為同等,取窗數最少者(避免用大量小窗堆分數)
      const maxScore = Math.max(...cands.map((c) => c.ev.score));
      const near = cands.filter((c) => c.ev.score >= maxScore - SCORE_TOLERANCE);
      near.sort((a, b) => a.windows.length - b.windows.length || b.ev.score - a.ev.score);
      for (const c of cands) c.maxScore = maxScore;
      best.push(near[0]);
      extras.push(...cands.filter((c) => c !== near[0]));
    }
  }
  const order = (a, b) => b.ev.score - a.ev.score;
  best.sort(order);
  const want = params.max_proposals || 3;
  const chosen = best.slice(0, want);
  if (chosen.length < want) {
    // 配置種類不夠:補上同配置的其他窗型,每個窗型組合只出現一次
    extras.sort(order);
    for (const c of extras) {
      if (chosen.length >= want) break;
      chosen.push(c);
    }
  }
  return {
    room,
    wind: { location: wind.location, period: wind.period, comfort: wind.comfort, usable_fraction: round(wind.usable_fraction, 3), monthly_usable_hours: wind.monthly_usable_hours, hours: wind.hours, mean_speed_m_s: round(wind.mean_speed_m_s, 3), prevailing: wind.prevailing },
    proposals: chosen.map((c, i) => toProposal(c, i + 1, ctx)),
    rejected_count: rejected.length,
    rejected_sample: rejected.slice(0, 10),
  };
}

function toProposal(c, rank, ctx) {
  const { room, wind, wallInfo, o } = ctx;
  const { ev, type, windows } = c;
  const walls = Object.values(ev.byWall)
    .map((a) => `${a.orientation}向牆(${a.wall_id})${a.n} 扇(方向分數 ${wallInfo[a.wall_id].score.toFixed(2)},窗牆比 ${(a.wwr * 100).toFixed(1)}%)`)
    .join('、');
  const reason =
    `${c.scheme.name} 相對外牆開窗形成穿堂風,連線避開服務核;${wind.location.city} ${wind.period}(僅統計${wind.comfort ? ` ${wind.comfort.t_min_c}–${wind.comfort.t_max_c}°C、${wind.comfort.hour_from}–${wind.comfort.hour_to} 時的可開窗時數` : '全部時數'})風資料,常見風向 ${wind.prevailing.join('/')}。` +
    `採 ${type.family_name} ${type.type_name} 共 ${windows.length} 扇:${walls}。` +
    `${ev.facing ? `其中 ${ev.facing} 扇因空間不足只能正對服務核;` : '所有窗皆避開正對服務核的牆段;'}` +
    `法定有效通風面積(PoC 暫算)${ev.legal.toFixed(2)} m² ≥ 辦公區 5% = ${ev.required.toFixed(2)} m²;EEWH VP=${ev.vp.toFixed(3)}(${ev.vpScore} 分);達標即止,不追求最大窗。`;
  const stripped = windows.map(({ plan_xy_m, ...rest }) => rest);
  return {
    proposal_id: `P${rank}`, rank, scheme: c.scheme.name, wall_ids: c.scheme.wall_ids,
    score: round(ev.score, 4),
    score_components: { orientation_score_area_weighted: round(ev.orient, 4), vp_saturated: round(Math.min(1, ev.vp / o.vp_target), 4), away_from_core: round(ev.awayCore, 4), weights: o.weights },
    window_count: windows.length,
    family_type: `${type.family_name}: ${type.type_name}`,
    windows: stripped,
    checks: {
      'VC-001': { status: 'provisional_assumption', result: '辦公居室、可開啟、直接對外(以 family 類型假設)' },
      'VC-002': { status: 'provisional_assumption', assumption_flag: true, result: `Σ寬×高×開啟比例 = ${ev.legal.toFixed(3)} m²` },
      'VC-003': { status: ev.legalPass ? 'pass' : 'fail', result: { legal_effective_open_area_m2: round(ev.legal, 3), required_m2: round(ev.required, 3), room_area_m2: room.floor_area_m2 } },
      'VC-004': { status: 'provisional_assumption', result: { VP: round(ev.vp, 4), score: ev.vpScore, Acj_m2: round(ev.acj, 3), Avi_m2: round(ev.avi, 3) } },
      'VC-005': { status: 'provisional_assumption', result: { Q_m3_h: Math.round(ev.q), note: 'PoC 相對比較,非法規值' } },
      'VC-006': { status: 'requires_verification', result: '水平位置已避開柱與轉角;垂直範圍、碰撞、3D 宿主切割尚未驗證,建窗後須回讀 type 與 Sill Height' },
    },
    per_wall: Object.fromEntries(Object.values(ev.byWall).map((a) => [a.wall_id, { orientation: a.orientation, count: a.n, gross_area_m2: round(a.gross, 3), wwr: round(a.wwr, 4), wind_score: round(wallInfo[a.wall_id].score, 4) }])),
    reason, assumption_flag: true, requires_user_confirmation: true,
    // 選窗型與窗數的過程:各窗型試算結果 + 選定窗型的逐步加窗紀錄(手動方案沒有)
    ...(c.typeTrace && {
      selection: {
        score_tolerance: SCORE_TOLERANCE,
        best_score_in_scheme: round(c.maxScore, 4),
        chosen_type: typeLabel(type),
        steps: c.steps,
        candidates: c.typeTrace.map(({ steps, ...t }) => ({ ...t, chosen: t.type === typeLabel(type), within_tolerance: t.score != null && t.score >= c.maxScore - SCORE_TOLERANCE })),
      },
    }),
  };
}

// 各外牆、各窗型最多能放幾扇(手動調整的上限)
export function wallCapacities(inputs, params = {}) {
  const { o, room, catalog, exterior } = prepare(inputs, params);
  return {
    types: catalog.map((t) => ({ type: typeLabel(t), width_m: t.width_m, height_m: t.height_m, opening_ratio: round(t.opening_ratio, 4) })),
    walls: exterior.map((w) => ({
      wall_id: w.wall_id,
      orientation: compass8(w.orientation.azimuth_deg),
      length_m: w.length_m,
      capacity: Object.fromEntries(catalog.map((t) => {
        if (!fitsHeight(t, w, o)) return [typeLabel(t), 0];
        let k = 0;
        while (k < 60 && placeOnWall(w, t, k + 1, room.cores, o)) k++;
        return [typeLabel(t), k];
      })),
    })),
  };
}

// 手動方案:per_wall = { 牆ID: { count, type: "族群: 類型" } },各牆可用不同窗型;沿用自動方案的排窗與檢核
export function customProposal(inputs, perWall, params = {}) {
  const { o, room, catalog, wallInfo, ctx } = prepare(inputs, params);
  const groups = [];
  for (const [wallId, { count, type }] of Object.entries(perWall)) {
    const n = Number(count);
    if (!n) continue;
    const info = wallInfo[wallId];
    if (!info) throw new Error(`牆 ${wallId} 不是可開窗的外牆`);
    const t = catalog.find((x) => typeLabel(x) === type);
    if (!t) throw new Error(`沒有窗型 ${type}`);
    if (!fitsHeight(t, info.wall, o)) throw new Error(`${type} 太高:窗台 ${o.sill_height_m} m + 窗高 ${t.height_m} m 超過牆高扣掉樑板 ${info.wall.height_m - o.head_margin_m} m`);
    const g = placeOnWall(info.wall, t, n, room.cores, o);
    if (!g) throw new Error(`${info.label} 牆放不下 ${n} 扇 ${type}`);
    groups.push({ g, t });
  }
  if (!groups.length) throw new Error('至少要有 1 扇窗');
  const windows = groups.flatMap((x) => x.g);
  const ev = evaluate(windows, ctx);
  const types = [...new Set(groups.map((x) => typeLabel(x.t)))];
  const ids = groups.map((x) => x.g[0].wall_id);
  const scheme = { name: ids.map((id) => wallInfo[id].label).join('-'), wall_ids: ids };
  const p = toProposal({ scheme, type: groups[0].t, windows, ev }, null, ctx);
  p.family_type = types.join(' + ');
  p.reason =
    `手動調整:${groups.map((x) => `${wallInfo[x.g[0].wall_id].label}向牆 ${x.g.length} 扇 ${typeLabel(x.t)}`).join('、')}。` +
    `法定有效通風面積(PoC 暫算)${ev.legal.toFixed(2)} m² ${ev.legalPass ? '≥' : '<'} 辦公區 5% = ${ev.required.toFixed(2)} m²;` +
    `EEWH VP=${ev.vp.toFixed(3)}(${ev.vpScore} 分);最大窗牆比 ${(ev.wwrMax * 100).toFixed(1)}%${ev.wwrPass ? '' : `,超過上限 ${o.max_wwr_per_wall * 100}%`}` +
    `${ev.facing ? `;其中 ${ev.facing} 扇正對服務核` : ''}。`;
  p.warnings = [
    !ev.legalPass && '未達法定 5% 有效通風面積',
    ev.vp < o.vp_target && `VP ${ev.vp.toFixed(3)} 未達 ${o.vp_target}`,
    !ev.wwrPass && `窗牆比超過 ${o.max_wwr_per_wall * 100}%`,
    ev.facing && `${ev.facing} 扇正對服務核`,
  ].filter(Boolean);
  return p;
}
