// 數字格式與 Excel 計算書一致：碳排千分位、CFR 兩位小數百分比（控制台計畫書第 6 節）。
// 讀不到的欄位顯示「—」而不是報錯。

const DASH = '—'
const isNum = (v) => typeof v === 'number' && Number.isFinite(v)

export const kg = (v) => (isNum(v) ? Math.round(v).toLocaleString('zh-TW') : DASH)
export const num = (v, digits = 2) =>
  isNum(v) ? v.toLocaleString('zh-TW', { minimumFractionDigits: digits, maximumFractionDigits: digits }) : DASH
export const pct = (v) => (isNum(v) ? `${(v * 100).toFixed(2)}%` : DASH)
export const text = (v) => (v === null || v === undefined || v === '' ? DASH : String(v))

export function time(iso) {
  if (!iso) return DASH
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso
  return d.toLocaleString('zh-TW', { year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hour12: false })
}

// 等級顏色只表達好壞：達到公有建築門檻（2 級以上）為 good，未達為 warn。
const PASSING = new Set(['1+級', '1級', '2級'])
export const gradeTone = (grade) => (grade ? (PASSING.has(grade) ? 'good' : 'warn') : 'none')

export const STRUCTURE = { Masonry: '磚石構造', RC: 'RC 構造', SRC: 'SRC 構造', Steel: 'S 構造', LightSteel: '輕鋼構造', Wood: '木構造' }
export const WEAR = { High: '高耗損', Mid: '中耗損', Low: '低耗損' }
export const WASTE = { Residential: '住宿類與旅館、醫院', Factory: '廠房類', Other: '其他非住宿類' }
