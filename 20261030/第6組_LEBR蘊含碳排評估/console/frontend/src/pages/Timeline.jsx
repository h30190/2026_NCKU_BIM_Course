import { excelUrl } from '../api.js'
import { kg, pct, time } from '../format.js'
import GradeBadge from '../components/GradeBadge.jsx'

export const MAX_COMPARE = 3

// 快照時間軸：某案子所有快照由新到舊，勾選 2 ~ 3 筆進入比較（W3、W5）。
export default function Timeline({ snapshots, selected, onToggle, onOpen, onCompare }) {
  const full = selected.length >= MAX_COMPARE
  return (
    <section className="card">
      <div className="card-head">
        <div>
          <h2>快照時間軸</h2>
          <p className="muted small">由新到舊。勾選 2～3 筆比較方案；點名稱看該筆總覽。</p>
        </div>
        <button type="button" className="primary" disabled={selected.length < 2} onClick={onCompare}>
          比較所選（{selected.length}／{MAX_COMPARE}）
        </button>
      </div>
      <div className="table-scroll">
        <table className="data-table">
          <thead>
            <tr>
              <th><span className="sr-only">選取</span></th>
              <th>快照</th>
              <th>等級</th>
              <th className="num">CFR</th>
              <th className="num">EEC kgCO2e</th>
              <th>時間</th>
              <th>計算書</th>
            </tr>
          </thead>
          <tbody>
            {snapshots.map((s) => {
              const checked = selected.includes(s.id)
              return (
                <tr key={s.id} className={checked ? 'is-checked' : ''}>
                  <td>
                    <input type="checkbox" checked={checked} disabled={!checked && full}
                      aria-label={`選取 ${s.name} 比較`} onChange={() => onToggle(s.id)} />
                  </td>
                  <td className="wrap">
                    <button type="button" className="link" onClick={() => onOpen(s.id)}>{s.name}</button>
                  </td>
                  <td><GradeBadge grade={s.grade} size="sm" /></td>
                  <td className="num">{pct(s.cfr)}</td>
                  <td className="num">{kg(s.eec)}</td>
                  <td>{time(s.createdAt)}</td>
                  <td>{s.hasExcel ? <a href={excelUrl(s.id)} download>下載</a> : <span className="muted">—</span>}</td>
                </tr>
              )
            })}
          </tbody>
        </table>
      </div>
      {full && <p className="muted small">最多比較 {MAX_COMPARE} 筆，取消勾選其中一筆才能換。</p>}
    </section>
  )
}
