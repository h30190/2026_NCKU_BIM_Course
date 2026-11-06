// 氣象分析:風花圖、逐月風速表、各外牆迎風分數的計算過程。
// 只讀取組員 3 引擎的 parseEpw / deriveOfficeRoom,不修改引擎;迎風分數公式與 engine_member3/epw.mjs 的 windModel 相同。
import fs from 'node:fs';
import { parseEpw, DEFAULT_COMFORT, compass8 } from '../engine_member3/epw.mjs';
import { getModel, safePath } from './catalog.mjs';

const CALM_M_S = 0.5; // 與 epw.mjs 相同
const DIRS16 = ['N', 'NNE', 'NE', 'ENE', 'E', 'ESE', 'SE', 'SSE', 'S', 'SSW', 'SW', 'WSW', 'W', 'WNW', 'NW', 'NNW'];
const SPEED_BINS = [[0.5, 2], [2, 4], [4, 6], [6, Infinity]]; // m/s
const sector16 = (dir) => Math.round((((dir % 360) + 360) % 360) / 22.5) % 16;
const r = (x, n = 3) => Math.round(x * 10 ** n) / 10 ** n;

function comfortOf(o) {
  if (o.all_hours) return null;
  return { ...DEFAULT_COMFORT, ...Object.fromEntries(['t_min_c', 't_max_c', 'hour_from', 'hour_to'].filter((k) => o[k] != null).map((k) => [k, Number(o[k])])) };
}

// 與 windModel 相同的篩選:指定月份 + 可開窗氣溫與時段
function load(epwPath, o) {
  const epw = parseEpw(fs.readFileSync(safePath(epwPath), 'utf8'));
  const months = o.months?.length ? o.months.map(Number) : null;
  const comfort = comfortOf(o);
  const inMonth = (h) => !months || months.includes(h.month);
  const usable = (h) => inMonth(h) && (!comfort || (h.temp != null && h.temp >= comfort.t_min_c && h.temp <= comfort.t_max_c && h.hour >= comfort.hour_from && h.hour <= comfort.hour_to));
  const hrs = epw.hours.filter(usable);
  if (!hrs.length) throw new Error('篩選後沒有可開窗的時數,請放寬月份或溫度條件');
  return { epw, months, comfort, hrs, inMonth };
}

function windrose(hrs) {
  const counts = DIRS16.map(() => SPEED_BINS.map(() => 0));
  let calm = 0;
  for (const h of hrs) {
    if (h.spd < CALM_M_S) { calm++; continue; }
    const b = SPEED_BINS.findIndex(([lo, hi]) => h.spd >= lo && h.spd < hi);
    counts[sector16(h.dir)][b]++;
  }
  const n = hrs.length;
  return {
    hours: n,
    calm_fraction: r(calm / n, 4),
    speed_bins_m_s: SPEED_BINS.map(([lo, hi]) => (hi === Infinity ? `≥${lo}` : `${lo}–${hi}`)),
    sectors: DIRS16.map((d, i) => {
      const sh = hrs.filter((h) => h.spd >= CALM_M_S && sector16(h.dir) === i);
      return {
        dir: d,
        azimuth_deg: i * 22.5,
        fractions: counts[i].map((c) => r(c / n, 4)),
        fraction: r(counts[i].reduce((a, b) => a + b, 0) / n, 4),
        mean_speed_m_s: sh.length ? r(sh.reduce((s, h) => s + h.spd, 0) / sh.length, 2) : null,
      };
    }),
  };
}

function monthly(epw, hrs, inMonth) {
  return Array.from({ length: 12 }, (_, i) => {
    const m = i + 1;
    const all = epw.hours.filter((h) => h.month === m);
    const use = hrs.filter((h) => h.month === m);
    const moving = use.filter((h) => h.spd >= CALM_M_S);
    const sec = Array(8).fill(0);
    for (const h of moving) sec[Math.round(h.dir / 45) % 8]++;
    const top = Math.max(...sec);
    return {
      month: m,
      selected: inMonth({ month: m }),
      usable_hours: use.length,
      usable_fraction: all.length ? r(use.length / all.length, 3) : 0,
      mean_speed_m_s: use.length ? r(use.reduce((s, h) => s + h.spd, 0) / use.length, 2) : null,
      max_speed_m_s: use.length ? Math.max(...use.map((h) => h.spd)) : null,
      calm_fraction: use.length ? r((use.length - moving.length) / use.length, 3) : null,
      prevailing: top ? ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'].filter((_, j) => sec[j] === top) : [],
      mean_temp_c: use.length ? r(use.reduce((s, h) => s + h.temp, 0) / use.length, 1) : null,
    };
  });
}

// 迎風值 E(az) = 平均[ 風速 × max(0, cos(風來向 − 牆外法線方位)) ],靜風(<0.5 m/s)貢獻 0 但計入分母
const exposureOf = (hrs, az) => {
  let s = 0;
  for (const h of hrs) if (h.spd >= CALM_M_S) s += h.spd * Math.max(0, Math.cos(((h.dir - az) * Math.PI) / 180));
  return s / hrs.length;
};

export function weatherAnalysis(epwPath, o = {}) {
  const { epw, months, comfort, hrs, inMonth } = load(epwPath, o);
  return {
    epw_file: epwPath,
    location: epw.location,
    period: months ? `months ${months.join(',')}` : 'annual',
    open_window_filter: comfort,
    usable_hours: hrs.length,
    mean_speed_m_s: r(hrs.reduce((s, h) => s + h.spd, 0) / hrs.length, 2),
    windrose: windrose(hrs),
    windrose_all_hours: windrose(epw.hours.filter(inMonth)),
    monthly: monthly(epw, hrs, inMonth),
  };
}

// 各外牆迎風分數的計算過程:原始迎風值、全方位最大值、正規化、各風向貢獻
export function exposureAnalysis(modelId, epwPath, o = {}) {
  const { hrs } = load(epwPath, o);
  const curve = Array.from({ length: 360 }, (_, a) => exposureOf(hrs, a));
  const max = Math.max(...curve);
  const maxAz = curve.indexOf(max);
  const n = hrs.length;
  const walls = getModel(modelId).building.walls.filter((w) => w.is_exterior && w.available_window_intervals?.length).map((w) => {
    const az = w.orientation.azimuth_deg;
    const contrib = DIRS16.map(() => 0);
    for (const h of hrs) if (h.spd >= CALM_M_S) contrib[sector16(h.dir)] += (h.spd * Math.max(0, Math.cos(((h.dir - az) * Math.PI) / 180))) / n;
    const raw = exposureOf(hrs, az);
    return {
      wall_id: w.wall_id,
      orientation: compass8(az),
      azimuth_deg: az,
      length_m: w.length_m,
      raw_exposure_m_s: r(raw, 4),
      score: r(max > 0 ? raw / max : 0, 4),
      contributions: DIRS16.map((d, i) => ({ dir: d, m_s: r(contrib[i], 4), share: raw > 0 ? r(contrib[i] / raw, 4) : 0 })).filter((c) => c.m_s > 0).sort((a, b) => b.m_s - a.m_s),
    };
  });
  return {
    formula: '迎風值 E(θ) = Σ[風速 × max(0, cos(風來向 − θ))] ÷ 可開窗時數;靜風 < 0.5 m/s 貢獻 0。分數 = E(牆外法線方位) ÷ max E(0–359°)',
    usable_hours: n,
    max_exposure_m_s: r(max, 4),
    max_azimuth_deg: maxAz,
    curve: curve.filter((_, a) => a % 5 === 0).map((e, i) => ({ azimuth_deg: i * 5, score: r(e / max, 4) })),
    walls,
  };
}
