import { kg, num, pct, time } from '../format.js'
import GradeBadge from '../components/GradeBadge.jsx'
import StageChart, { SEGMENTS } from '../components/StageChart.jsx'

// W5 方案比較：2 ~ 3 欄並排等級、CFR、四階段碳排與差值；非主結構依類別比較，標出差異最大的前三項。
// 差值都是「該快照 − 快照 1」，由後端 /api/compare 算好，網頁只顯示。
export default function Compare({ data, onBack }) {
  if (!data) return <p className="muted">載入中…</p>
  const snaps = data.snapshots
  const tag = (i) => `快照 ${i + 1}`
  const stageLabel = (key) => SEGMENTS.find((s) => s.key === key)?.label

  return (
    <div className="overview">
      <div className="overview-head">
        <div>
          <h1>方案比較</h1>
          <p className="muted small">差值 = 各快照 − 快照 1。負值代表比快照 1 碳排少。</p>
        </div>
        <button type="button" onClick={onBack}>回快照時間軸</button>
      </div>

      <div className="compare-cards" style={{ '--cols': snaps.length }}>
        {snaps.map((s, i) => (
          <section key={s.id} className="card compare-card">
            <span className="muted small">{tag(i)}{i === 0 ? '（比較基準）' : ''}</span>
            <h2 className="compare-name">{s.name}</h2>
            <p className="muted small">{s.projectTitle}・{time(s.createdAt)}</p>
            <div className="compare-hero">
              <GradeBadge grade={s.grade} size="md" />
              <span className="compare-cfr">{pct(s.cfr)}</span>
            </div>
            <dl className="compare-stats">
              <div><dt className="muted small">EEC</dt><dd>{kg(s.eec)}</dd></div>
              <div><dt className="muted small">ECI</dt><dd>{num(s.eci)}</dd></div>
              {i > 0 && (
                <div>
                  <dt className="muted small">CFR 差</dt>
                  <dd><Delta value={(s.cfr - snaps[0].cfr) * 100} digits={2} unit=" 個百分點" goodWhenUp /></dd>
                </div>
              )}
            </dl>
          </section>
        ))}
      </div>

      <section className="card">
        <h2>設計案四階段碳排</h2>
        <StageChart
          rows={snaps.map((s, i) => ({
            key: `s${s.id}`,
            label: tag(i),
            data: Object.fromEntries(data.stages.filter((r) => r.key !== 'total').map((r) => [r.key, r.values[i]])),
          }))}
          caption="單位 kgCO2e。只比設計案；各快照的基準案依自己的構件產生，不一定相同。"
        />
        <CompareTable
          rows={data.stages.map((r) => ({ ...r, label: r.key === 'total' ? '合計' : stageLabel(r.key) }))}
          snaps={snaps} tag={tag} totalKey="total"
        />
      </section>

      <section className="card">
        <h2>非主結構依類別比較（設計案）</h2>
        <p className="muted small">每類為新建＋更新碳排合計。標「差異第 N」的是各快照間差距最大的前三類。</p>
        <CompareTable rows={data.categories} snaps={snaps} tag={tag} showRank />
      </section>
    </div>
  )
}

function CompareTable({ rows, snaps, tag, totalKey, showRank }) {
  const body = rows.filter((r) => r.key !== totalKey)
  const total = rows.find((r) => r.key === totalKey)
  const line = (r, strong) => (
    <>
      {r.values.map((v, i) => <td key={`v${i}`} className={`num ${strong ? 'strong' : ''}`}>{kg(v)}</td>)}
      {r.diffs.slice(1).map((d, i) => <td key={`d${i}`} className="num"><Delta value={d} /></td>)}
    </>
  )
  return (
    <div className="table-scroll">
      <table className="data-table compact">
        <thead>
          <tr>
            <th>項目</th>
            {snaps.map((s, i) => <th key={s.id} className="num" title={s.name}>{tag(i)}</th>)}
            {snaps.slice(1).map((s, i) => <th key={`d${s.id}`} className="num">{tag(i + 1)} − {tag(0)}</th>)}
          </tr>
        </thead>
        <tbody>
          {body.length === 0 && <tr><td colSpan={snaps.length * 2} className="muted">沒有資料</td></tr>}
          {body.map((r) => (
            <tr key={r.key} className={showRank && r.rank ? 'is-ranked' : ''}>
              <td className="wrap">
                {r.label}
                {showRank && r.rank > 0 && <span className="rank-tag">差異第 {r.rank}</span>}
              </td>
              {line(r)}
            </tr>
          ))}
        </tbody>
        {total && (
          <tfoot>
            <tr><td>{total.label}</td>{line(total, true)}</tr>
          </tfoot>
        )}
      </table>
    </div>
  )
}

// 差值：碳排變少（負值）是好事；CFR 變高是好事。正負號與文字一起表達，不只靠顏色。
function Delta({ value, digits = 0, unit = '', goodWhenUp = false }) {
  if (typeof value !== 'number' || !Number.isFinite(value)) return '—'
  const zero = Math.abs(value) < 10 ** -digits / 2
  if (zero) return <span className="muted">0{unit}</span>
  const good = goodWhenUp ? value > 0 : value < 0
  const text = Math.abs(value).toLocaleString('zh-TW', { minimumFractionDigits: digits, maximumFractionDigits: digits })
  return <span className={good ? 'delta-good' : 'delta-bad'}>{value > 0 ? '+' : '−'}{text}{unit}</span>
}
