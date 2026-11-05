import { excelUrl } from '../api.js'
import { kg, num, pct, text, time, STRUCTURE, WEAR, WASTE } from '../format.js'
import GradeBadge from '../components/GradeBadge.jsx'
import GradeScale from '../components/GradeScale.jsx'
import StageChart, { SEGMENTS } from '../components/StageChart.jsx'
import ItemsTable from '../components/ItemsTable.jsx'

// W4 評估總覽：等級與 CFR、EEC／EECc／ECI、八等級刻度、四階段碳排、建築參數、非主結構明細。
export default function Overview({ snapshot, snapshots, snapshotId, onSelectSnapshot }) {
  if (!snapshot) return <p className="muted">載入中…</p>
  const r = snapshot.result ?? {}
  const p = snapshot.inputs ?? {}
  const shape = snapshot.shape ?? {}

  return (
    <div className="overview">
      <div className="overview-head">
        <div>
          <h1>{snapshot.projectTitle}</h1>
          <p className="muted small">
            快照時間 {time(snapshot.createdAt)}・外掛 {text(snapshot.pluginVersion)}・手冊 {text(snapshot.manual)}
          </p>
        </div>
        <div className="head-actions">
          {snapshot.hasExcel && <a className="button" href={excelUrl(snapshot.id)} download>下載 Excel 計算書</a>}
          <label className="snapshot-picker">
            <span className="muted small">快照</span>
            <select value={snapshotId ?? ''} onChange={(e) => onSelectSnapshot(Number(e.target.value))}>
              {snapshots.map((s) => (
                <option key={s.id} value={s.id}>{s.name}（{s.grade}，{time(s.createdAt)}）</option>
              ))}
            </select>
          </label>
        </div>
      </div>

      <section className="card hero">
        <div className="hero-grade">
          <span className="muted small">LEBR 等級</span>
          <GradeBadge grade={r.grade} size="lg" />
        </div>
        <div className="hero-cfr">
          <span className="muted small">碳排減碳率 CFR</span>
          <span className="hero-number">{pct(r.cfr)}</span>
          <span className="muted small">（EECc − EEC）÷ EECc</span>
        </div>
        <dl className="stat-grid">
          <Stat label="設計案 EEC" value={kg(r.eec)} unit="kgCO2e" />
          <Stat label="基準案 EECc" value={kg(r.eecc)} unit="kgCO2e" />
          <Stat label="減碳量 ΔCF" value={kg(r.deltaCf)} unit="kgCO2e" />
          <Stat label="碳排密度 ECI" value={num(r.eci)} unit="kgCO2e/m²" />
          <Stat label="基準 ECIs" value={num(r.ecis)} unit="kgCO2e/m²" />
          <Stat label="全建築 TEC" value={kg(r.tec)} unit="kgCO2e（含地下層，不列入分級）" />
        </dl>
      </section>

      <section className="card">
        <h2>八等級刻度（表 12）</h2>
        <GradeScale result={r} />
      </section>

      <section className="card">
        <h2>地上層四階段碳排</h2>
        <StageChart
          rows={r.stages && [
            { key: 'design', label: '設計案', data: r.stages.design },
            { key: 'baseline', label: '基準案', data: r.stages.baseline },
          ]}
          caption="單位 kgCO2e。施工與拆除廢棄只以設計案計算一次，基準案同（手冊式 12～19）。"
        />
        <StageTable stages={r.stages} ll={r.ll} credits={r.credits} />
      </section>

      <section className="card">
        <h2>非主結構明細</h2>
        <ItemsTable items={snapshot.items} />
      </section>

      <section className="card">
        <h2>建築參數</h2>
        <dl className="param-grid">
          <Param label="地上層樓地板面積 AFu" value={`${num(p.aFu)} m²`} />
          <Param label="地下層樓地板面積 AFb" value={`${num(p.aFb)} m²`} />
          <Param label="地上／地下樓層數 S／Sb" value={`${text(p.s)}／${text(p.sb)}`} />
          <Param label="一樓樓高 BH" value={`${num(p.bh)} m`} />
          <Param label="構造類別" value={STRUCTURE[p.structure] ?? text(p.structure)} />
          <Param label="構造係數 W（基準 W）" value={`${num(p.w, 2)}（${num(p.baselineW, 2)}）`} />
          <Param label="跨距變化係數 Sp（基準）" value={`${num(p.sp, 3)}（${num(p.baselineSp, 3)}）`} />
          <Param label="形狀係數 F（基準）" value={`${num(p.f, 3)}（${num(p.baselineF, 3)}）`} />
          <Param label="PAr／長寬比 b／出挑 Rc" value={`${num(shape.pAr, 3)}／${num(shape.aspect, 3)}／${num(shape.rc, 3)}`} />
          <Param label="I／SaD／Fu／Rs" value={`${text(p.i)}／${text(p.saD)}／${text(p.fu)}／${text(p.rs)}`} />
          <Param label="活載重 L／額外靜載重 D0" value={`${text(p.l)}／${text(p.d0)} kgf/m²`} />
          <Param label="CSER（LCCR）" value={`${text(p.cser)}（${num(p.lccr, 3)}）`} />
          <Param label="耗損等級" value={WEAR[p.wear] ?? text(p.wear)} />
          <Param label="廢棄物用途" value={WASTE[p.wasteUsage] ?? text(p.wasteUsage)} />
          <Param label="延壽優惠 LL" value={num(p.ll, 2)} />
        </dl>
      </section>
    </div>
  )
}

// 圖表的表格檢視：數字、差值，供對照 Excel 計算書與無法辨色時閱讀。
function StageTable({ stages, ll, credits }) {
  if (!stages?.design || !stages?.baseline) return null
  const total = (s) => SEGMENTS.reduce((a, seg) => a + (s[seg.key] ?? 0), 0)
  const d = stages.design
  const b = stages.baseline
  return (
    <div className="table-scroll">
      <table className="data-table compact">
        <thead>
          <tr><th>項目</th><th className="num">設計案</th><th className="num">基準案</th><th className="num">差值</th></tr>
        </thead>
        <tbody>
          {SEGMENTS.map((s, i) => (
            <tr key={s.key}>
              <td><span className={`swatch series-${i + 1}`} />{s.label}</td>
              <td className="num">{kg(d[s.key])}</td>
              <td className="num">{kg(b[s.key])}</td>
              <td className="num">{kg((d[s.key] ?? 0) - (b[s.key] ?? 0))}</td>
            </tr>
          ))}
        </tbody>
        <tfoot>
          <tr>
            <td>合計</td>
            <td className="num strong">{kg(total(d))}</td>
            <td className="num strong">{kg(total(b))}</td>
            <td className="num strong">{kg(total(d) - total(b))}</td>
          </tr>
        </tfoot>
      </table>
      {(ll > 0 || credits > 0) && (
        <p className="muted small">設計案 EEC =（合計 − 減碳優惠 {kg(credits)}）÷（1 + LL {num(ll, 2)}）</p>
      )}
    </div>
  )
}

function Stat({ label, value, unit }) {
  return (
    <div className="stat">
      <dt className="muted small">{label}</dt>
      <dd><span className="stat-value">{value}</span> <span className="muted small">{unit}</span></dd>
    </div>
  )
}

function Param({ label, value }) {
  return (
    <div className="param">
      <dt className="muted small">{label}</dt>
      <dd>{value}</dd>
    </div>
  )
}
