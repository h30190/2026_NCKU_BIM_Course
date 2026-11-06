// 讀取任意 EPW(台灣各地 TMYx 皆可,例如 Climate.OneBuilding.org 的 TWN_*.epw),計算任意牆面方位的風向適合度。
import fs from 'node:fs';

const CALM_M_S = 0.5; // 靜風門檻(與組員 1 的 weather_analysis.json 一致)
const COMPASS8 = ['N', 'NE', 'E', 'SE', 'S', 'SW', 'W', 'NW'];
export const compass8 = (az) => COMPASS8[Math.round((((az % 360) + 360) % 360) / 45) % 8];

export function parseEpw(text) {
  const lines = text.split(/\r?\n/);
  const loc = lines[0].split(',');
  if (loc[0] !== 'LOCATION') throw new Error('不是有效的 EPW:第一行應為 LOCATION');
  const start = lines.findIndex((l) => l.startsWith('DATA PERIODS')) + 1;
  const hours = [];
  for (const line of lines.slice(start)) {
    const f = line.split(',');
    if (f.length < 22) continue;
    const month = +f[1];
    const hour = +f[3];
    const temp = +f[6];
    const dir = +f[20];
    const spd = +f[21];
    if (!(month >= 1 && month <= 12) || !Number.isFinite(spd) || spd >= 999 || !Number.isFinite(dir)) continue; // 缺值
    hours.push({ month, hour, temp: temp < 99 ? temp : null, dir, spd });
  }
  if (!hours.length) throw new Error('EPW 沒有有效的風資料');
  return {
    location: { city: loc[1], country: loc[3], station: loc[5], lat: +loc[6], lon: +loc[7], tz: +loc[8], elevation_m: +loc[9] },
    hours,
  };
}

// 開窗時段篩選:冬季過冷、夏季過熱或非辦公時段不會開窗,這些時數不納入風向統計。
// months 省略 = 全年;comfort = null 表示不篩選。溫度缺值的時數視為不適合開窗。
export const DEFAULT_COMFORT = { t_min_c: 18, t_max_c: 30, hour_from: 8, hour_to: 22 }; // 示意值,待團隊確認

// 回傳 score(az):以 mean(v·max(0,cos(風來向−牆外法線))) 對所有方位掃描最大值正規化(0–1)。
export function windModel(epw, months = null, comfort = DEFAULT_COMFORT) {
  const usable = (h) =>
    (!months || months.includes(h.month)) &&
    (!comfort || (h.temp != null && h.temp >= comfort.t_min_c && h.temp <= comfort.t_max_c && h.hour >= comfort.hour_from && h.hour <= comfort.hour_to));
  const hrs = epw.hours.filter(usable);
  if (!hrs.length) throw new Error('篩選後沒有可開窗的時數,請放寬月份或溫度條件');
  const raw = (az) => {
    let s = 0;
    for (const h of hrs) if (h.spd >= CALM_M_S) s += h.spd * Math.max(0, Math.cos(((h.dir - az) * Math.PI) / 180));
    return s / hrs.length; // 靜風貢獻為 0,但仍計入分母
  };
  let max = 0;
  for (let a = 0; a < 360; a += 1) max = Math.max(max, raw(a));
  const meanSpeed = hrs.reduce((s, h) => s + h.spd, 0) / hrs.length;
  const sector = Array(8).fill(0);
  for (const h of hrs) if (h.spd >= CALM_M_S) sector[Math.round(h.dir / 45) % 8]++;
  const top = Math.max(...sector);
  return {
    location: epw.location,
    period: months ? `months ${months.join(',')}` : 'annual',
    comfort,
    hours: hrs.length,
    usable_fraction: hrs.length / epw.hours.filter((h) => !months || months.includes(h.month)).length,
    monthly_usable_hours: Object.fromEntries(Array.from({ length: 12 }, (_, i) => [i + 1, hrs.filter((h) => h.month === i + 1).length])),
    mean_speed_m_s: meanSpeed,
    prevailing: COMPASS8.filter((_, i) => sector[i] === top),
    score: (az) => (max > 0 ? raw(az) / max : 0),
    exposure_m_s: raw,
  };
}

export const loadWindFromEpw = (file, months = null, comfort = DEFAULT_COMFORT) => windModel(parseEpw(fs.readFileSync(file, 'utf8')), months, comfort);
