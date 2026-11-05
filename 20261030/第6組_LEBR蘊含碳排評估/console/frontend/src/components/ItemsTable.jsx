import { useMemo, useState } from 'react'
import { kg, num, pct, text } from '../format.js'

// W4 非主結構明細：B-LCC ID、類別、面積、碳排、佔比，可切換設計案／基準案，點欄名排序。
const COLUMNS = [
  { key: 'blccId', label: 'B-LCC ID' },
  { key: 'categoryName', label: '類別' },
  { key: 'name', label: '構件' },
  { key: 'area', label: '面積 m²', numeric: true },
  { key: 'newCarbon', label: '新建', numeric: true },
  { key: 'renewCarbon', label: '更新', numeric: true },
  { key: 'carbon', label: '合計', numeric: true },
  { key: 'share', label: '佔比', numeric: true },
]

export default function ItemsTable({ items }) {
  const [side, setSide] = useState('design')
  const [sort, setSort] = useState({ key: 'carbon', desc: true })

  const rows = useMemo(() => {
    const list = (items ?? []).filter((i) => i.side === side)
    const total = list.reduce((s, i) => s + (i.carbon ?? 0), 0)
    const withShare = list.map((i) => ({ ...i, share: total > 0 ? i.carbon / total : 0 }))
    const dir = sort.desc ? -1 : 1
    withShare.sort((a, b) => {
      const x = a[sort.key] ?? ''
      const y = b[sort.key] ?? ''
      return (typeof x === 'number' ? x - y : String(x).localeCompare(String(y), 'zh-Hant')) * dir
    })
    return { list: withShare, total, area: list.reduce((s, i) => s + (i.area ?? 0), 0),
      newSum: list.reduce((s, i) => s + (i.newCarbon ?? 0), 0), renewSum: list.reduce((s, i) => s + (i.renewCarbon ?? 0), 0) }
  }, [items, side, sort])

  const toggle = (key) => setSort((s) => (s.key === key ? { key, desc: !s.desc } : { key, desc: true }))

  return (
    <div>
      <div className="segmented" role="tablist" aria-label="設計案或基準案">
        {[['design', '設計案'], ['baseline', '基準案']].map(([k, label]) => (
          <button key={k} type="button" role="tab" aria-selected={side === k}
            className={side === k ? 'is-active' : ''} onClick={() => setSide(k)}>{label}</button>
        ))}
      </div>
      <div className="table-scroll">
        <table className="data-table">
          <thead>
            <tr>
              {COLUMNS.map((c) => (
                <th key={c.key} className={c.numeric ? 'num' : ''}
                  aria-sort={sort.key === c.key ? (sort.desc ? 'descending' : 'ascending') : 'none'}>
                  <button type="button" className="th-sort" onClick={() => toggle(c.key)}>
                    {c.label}{sort.key === c.key ? (sort.desc ? ' ▼' : ' ▲') : ''}
                  </button>
                </th>
              ))}
            </tr>
          </thead>
          <tbody>
            {rows.list.length === 0 && (
              <tr><td colSpan={COLUMNS.length} className="muted">沒有非主結構構件</td></tr>
            )}
            {rows.list.map((i, idx) => (
              <tr key={idx} title={i.source || undefined}>
                <td><code>{text(i.blccId)}</code></td>
                <td>{text(i.categoryName)}</td>
                <td className="wrap">{text(i.name)}</td>
                <td className="num">{num(i.area)}</td>
                <td className="num">{kg(i.newCarbon)}</td>
                <td className="num">{kg(i.renewCarbon)}</td>
                <td className="num strong">{kg(i.carbon)}</td>
                <td className="num">{pct(i.share)}</td>
              </tr>
            ))}
          </tbody>
          {rows.list.length > 0 && (
            <tfoot>
              <tr>
                <td colSpan={3}>合計</td>
                <td className="num">{num(rows.area)}</td>
                <td className="num">{kg(rows.newSum)}</td>
                <td className="num">{kg(rows.renewSum)}</td>
                <td className="num strong">{kg(rows.total)}</td>
                <td className="num">100.00%</td>
              </tr>
            </tfoot>
          )}
        </table>
      </div>
      <p className="muted small">碳排單位 kgCO2e。新建 = 面積 × B-LCC 新建碳排；更新 = 面積 × 更新碳排（依耗損等級）。滑鼠停在列上可看資料來源。</p>
    </div>
  )
}
