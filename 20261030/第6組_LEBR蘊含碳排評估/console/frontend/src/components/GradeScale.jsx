import { gradeTone, kg, num } from '../format.js'

// 表 12 八等級刻度尺：橫軸是碳排密度 ECI，7 個刻度（基準案 ECIs × 倍數）分出 8 個等級，標出本案位置。
const LO = 0.7
const HI = 1.3
const W = 640
const PAD = 16
const BAR_Y = 34
const BAR_H = 26

export default function GradeScale({ result }) {
  const ticks = result?.scale ?? []
  const ecis = result?.ecis
  if (!ticks.length || !ecis) return <p className="muted">—</p>

  const x = (ratio) => PAD + ((Math.min(Math.max(ratio, LO), HI) - LO) / (HI - LO)) * (W - 2 * PAD)
  const ratios = ticks.map((t) => t.eci / ecis)
  // 每個等級的範圍：1+ 級在第一個刻度以下，7 級在最後一個刻度以上
  const grades = [...ticks.map((t) => t.grade), '7級']
  const edges = [LO, ...ratios, HI]
  const bands = grades.map((g, i) => ({ grade: g, from: edges[i], to: edges[i + 1] }))
  const here = result.eci / ecis

  return (
    <figure className="figure">
      <svg viewBox={`0 0 ${W} 110`} className="grade-scale" role="img"
        aria-label={`八等級刻度：本案 ECI ${num(result.eci)}，落在 ${result.grade}`}>
        {bands.map((b, i) => {
          const isHere = b.grade === result.grade
          return (
            <g key={b.grade}>
              <rect x={x(b.from) + 1} y={BAR_Y} width={Math.max(x(b.to) - x(b.from) - 2, 1)} height={BAR_H}
                rx={i === 0 || i === bands.length - 1 ? 4 : 0}
                className={isHere ? `band band-here band-${gradeTone(result.grade)}` : 'band'} />
              <text x={(x(b.from) + x(b.to)) / 2} y={BAR_Y + BAR_H / 2 + 4} textAnchor="middle"
                className={isHere ? 'band-label band-label-here' : 'band-label'}>{b.grade}</text>
            </g>
          )
        })}
        {ticks.map((t, i) => (
          <text key={t.grade} x={x(ratios[i])} y={BAR_Y + BAR_H + 16} textAnchor="middle" className="tick-label">
            {kg(t.eci)}
          </text>
        ))}
        <g transform={`translate(${x(here)},0)`}>
          <line y1={BAR_Y - 6} y2={BAR_Y} className="marker-line" />
          <path d={`M -6 ${BAR_Y - 14} L 6 ${BAR_Y - 14} L 0 ${BAR_Y - 6} Z`} className="marker" />
          <text y={BAR_Y - 18} textAnchor={here > 1.2 ? 'end' : here < 0.8 ? 'start' : 'middle'} className="marker-label">
            本案 ECI {num(result.eci)}
          </text>
        </g>
        <text x={W - PAD} y={104} textAnchor="end" className="axis-note">刻度數字為碳排密度 kgCO2e/m²（基準案 ECIs {num(ecis)}）</text>
      </svg>
    </figure>
  )
}
