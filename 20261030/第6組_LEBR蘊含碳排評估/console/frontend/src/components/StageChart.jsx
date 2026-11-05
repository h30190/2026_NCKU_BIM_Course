import { useId, useState } from 'react'
import { kg, pct } from '../format.js'

// 四階段碳排堆疊長條。製造運輸拆成主結構與非主結構新建兩段，共五段。
// rows：[{ key, label, data }]，總覽傳設計案與基準案，方案比較傳各快照的設計案。
export const SEGMENTS = [
  { key: 'structure', label: '主結構', stage: '製造運輸' },
  { key: 'nonStructural', label: '非主結構新建', stage: '製造運輸' },
  { key: 'renew', label: '更新修繕', stage: '更新修繕' },
  { key: 'construction', label: '施工', stage: '施工' },
  { key: 'demolition', label: '拆除廢棄', stage: '拆除廢棄' },
]

const W = 640
const LABEL_W = 64
const VALUE_W = 72
const BAR_H = 28
const ROW_GAP = 18
const TOP = 8
const AXIS_H = 22
const GAP = 2

function niceStep(max) {
  const raw = max / 4
  const mag = 10 ** Math.floor(Math.log10(raw))
  const n = raw / mag
  return (n <= 1 ? 1 : n <= 2 ? 2 : n <= 5 ? 5 : 10) * mag
}

export default function StageChart({ rows, caption }) {
  const [tip, setTip] = useState(null)
  const uid = useId()
  if (!rows?.length || rows.some((r) => !r.data)) return <p className="muted">—</p>
  const totals = rows.map((r) => SEGMENTS.reduce((s, seg) => s + (r.data[seg.key] ?? 0), 0))
  const step = niceStep(Math.max(...totals))
  const max = Math.ceil(Math.max(...totals) / step) * step
  const plotW = W - LABEL_W - VALUE_W // 右側留給合計數字，不壓在長條上
  const sx = (v) => (v / max) * plotW
  const H = TOP + rows.length * (BAR_H + ROW_GAP) + AXIS_H
  const gridVals = Array.from({ length: Math.round(max / step) + 1 }, (_, i) => i * step)

  return (
    <figure className="figure">
      <ul className="legend" aria-label="圖例">
        {SEGMENTS.map((s, i) => (
          <li key={s.key}><span className={`swatch series-${i + 1}`} />{s.label}</li>
        ))}
      </ul>
      <div className="chart-wrap" onMouseLeave={() => setTip(null)}>
        <svg viewBox={`0 0 ${W} ${H}`} className="stage-chart" role="img"
          aria-label={rows.map((r, i) => `${r.label}合計 ${kg(totals[i])}`).join('，') + ' kgCO2e'}>
          {gridVals.map((v) => (
            <g key={v} transform={`translate(${LABEL_W + sx(v)},0)`}>
              <line y1={TOP - 4} y2={H - AXIS_H} className={v === 0 ? 'baseline-axis' : 'gridline'} />
              <text y={H - 6} textAnchor="middle" className="tick-label">{kg(v)}</text>
            </g>
          ))}
          {rows.map((r, ri) => {
            const y = TOP + ri * (BAR_H + ROW_GAP)
            let acc = 0
            const visible = SEGMENTS.map((s, i) => ({ ...s, i, v: r.data[s.key] ?? 0 })).filter((s) => s.v > 0)
            return (
              <g key={r.key}>
                <text x={LABEL_W - 8} y={y + BAR_H / 2 + 5} textAnchor="end" className="row-label">{r.label}</text>
                <clipPath id={`${uid}-clip-${r.key}`}>
                  <rect x={LABEL_W} y={y} width={sx(totals[ri])} height={BAR_H} rx={4} />
                </clipPath>
                <g clipPath={`url(#${CSS.escape(uid)}-clip-${r.key})`}>
                  {visible.map((s, vi) => {
                    const x0 = LABEL_W + sx(acc)
                    acc += s.v
                    const w = Math.max(sx(s.v) - (vi < visible.length - 1 ? GAP : 0), 0.5)
                    return (
                      <rect key={s.key} x={x0} y={y} width={w} height={BAR_H} className={`seg series-${s.i + 1}`}
                        onMouseMove={(e) => {
                          const box = e.currentTarget.ownerSVGElement.parentNode.getBoundingClientRect()
                          setTip({ x: e.clientX - box.left, y: e.clientY - box.top, row: r.label, seg: s, total: totals[ri] })
                        }} />
                    )
                  })}
                </g>
                <text x={LABEL_W + sx(totals[ri]) + 6} y={y + BAR_H / 2 + 5} className="value-label">
                  {kg(totals[ri])}
                </text>
              </g>
            )
          })}
        </svg>
        {tip && (
          <div className="tooltip" style={{ left: tip.x + 12, top: tip.y + 12 }}>
            <div className="tooltip-title">{tip.row}・{tip.seg.label}</div>
            <div>{kg(tip.seg.v)} kgCO2e（{pct(tip.seg.v / tip.total)}）</div>
            <div className="muted small">階段：{tip.seg.stage}</div>
          </div>
        )}
      </div>
      <figcaption className="muted small">{caption ?? '單位 kgCO2e。'}</figcaption>
    </figure>
  )
}
