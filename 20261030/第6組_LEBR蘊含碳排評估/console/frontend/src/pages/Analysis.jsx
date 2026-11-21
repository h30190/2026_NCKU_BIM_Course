import { useCallback, useEffect, useState } from 'react'
import { listProposals, setProposalStatus, deleteProposal } from '../api.js'
import { kg, pct, time } from '../format.js'
import GradeBadge from '../components/GradeBadge.jsx'
import SnapshotPicker from '../components/SnapshotPicker.jsx'

// 分析與建議（AI 分析計畫書 5、6 節）：哪裡佔比最多、每項還能降多少、距下一級，以及 AI 建議清單。
// 數字全部來自快照的 analysis（外掛用同一個計算核心逐項試算），網頁不另外計算碳排。
// 快照切換與評估總覽共用同一個選取；減碳建議屬於案件，不隨快照變。
export default function Analysis({ snapshot, snapshots, snapshotId, onSelectSnapshot }) {
  if (!snapshot) return <p className="muted">載入中…</p>
  const a = snapshot.analysis
  const r = snapshot.result ?? {}

  return (
    <div className="overview">
      <div className="overview-head">
        <div>
          <h1>{snapshot.projectTitle}・分析與建議</h1>
          <p className="muted small">快照「{snapshot.name}」・{time(snapshot.createdAt)}</p>
        </div>
        <div className="head-actions">
          <SnapshotPicker snapshots={snapshots} snapshotId={snapshotId} onSelect={onSelectSnapshot} />
        </div>
      </div>

      {!a ? (
        <div className="notice">
          <h2>這筆快照沒有分析資料</h2>
          <p>舊版外掛上傳的快照不含分析。用新版外掛在 Revit 按「計算」→「上傳到控制台」，或用命令列重新產生：</p>
          <pre>dotnet run --project LebrCarbon\src\LebrCarbon.Cli -- 專案1.lebr.json --upload</pre>
        </div>
      ) : (
        <>
          <NextGradeCard result={r} next={a.nextGrade} potential={a.potential} />
          <ContributionCard items={a.contributions} />
          <PotentialCard potential={a.potential} contributions={a.contributions} />
        </>
      )}
      <ProposalsCard project={snapshot.projectKey} current={r} />
    </div>
  )
}

function NextGradeCard({ result, next, potential }) {
  // 帷幕牆基準案規則還沒確認的項目不當頭條
  const best = potential.filter((p) => !p.needsRuleCheck).map((p) => p.best).filter(Boolean)
    .sort((x, y) => y.deltaCfr - x.deltaCfr)[0]
  const pending = potential.some((p) => p.needsRuleCheck && p.best)
  return (
    <section className="card hero">
      <div className="hero-grade">
        <span className="muted small">目前等級</span>
        <GradeBadge grade={result.grade} size="lg" />
      </div>
      <div className="hero-cfr">
        <span className="muted small">{next ? `距 ${next.grade}（CFR > ${pct(next.cfrNeeded)}）` : '已是最高等級'}</span>
        <span className="hero-number">{next ? `差 ${pts(next.gap)}` : pct(result.cfr)}</span>
        <span className="muted small">目前 CFR {pct(result.cfr)}</span>
      </div>
      <p className="small">
        {best
          ? <>單項替換最多可提升 <strong>{pts(best.deltaCfr)}</strong>（換成 {best.blccId} {best.name}，{best.grade}）。
              {next && best.deltaCfr < next.gap && ' 單換一項還不夠升級，需要組合多項替換。'}</>
          : '目前各構件換成同類別其他選項都不會提高減碳率。'}
        {pending && <span className="muted"> 帷幕牆替換待確認手冊式 11 的基準案規則，未列入。</span>}
      </p>
    </section>
  )
}

// 一列一個構件，依碳排由大到小；單一系列不需圖例，數值直接標在長條右側。
function ContributionCard({ items }) {
  const rows = [...items].sort((x, y) => y.designTotal - x.designTotal)
  const max = Math.max(...rows.map((c) => c.designTotal), 1)
  return (
    <section className="card">
      <h2>哪裡佔比最多</h2>
      <p className="muted small">非主結構各構件的新建 ＋ 更新碳排，百分比為佔非主結構合計。主結構由迴歸公式計算，不能逐項替換，不列在這裡。</p>
      <ul className="hbars" aria-label="非主結構碳排佔比">
        {rows.map((c) => (
          <li key={c.key} title={`${c.blccId ?? '自訂'} ${c.name}\n新建 ${kg(c.designNew)}、更新 ${kg(c.designRenew)} kgCO2e\n${c.source}`}>
            <span className="hbar-label">{c.categoryName}<span className="muted small"> {c.blccId}</span></span>
            <span className="hbar-track">
              <span className="hbar-fill" style={{ width: `${(c.designTotal / max) * 100}%` }} />
            </span>
            <span className="hbar-value">{kg(c.designTotal)}<span className="muted small"> {pct(c.shareOfNonStructural)}</span></span>
          </li>
        ))}
      </ul>
      <div className="table-scroll">
        <table className="data-table compact">
          <thead>
            <tr><th>構件</th><th>來源</th><th className="num">面積 m²</th><th className="num">設計案</th><th className="num">基準案</th><th className="num">佔全案</th></tr>
          </thead>
          <tbody>
            {rows.map((c) => (
              <tr key={c.key}>
                <td>{c.blccId} {c.name}</td>
                <td className="wrap muted small">{c.source}</td>
                <td className="num">{c.area.toFixed(2)}</td>
                <td className="num">{kg(c.designTotal)}</td>
                <td className="num">{c.baselineTotal == null ? '—' : `${kg(c.baselineTotal)}（${c.baselineBlccId}）`}</td>
                <td className="num">{pct(c.shareOfTotal)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  )
}

// 每項「只換這一項、其他不動」的單項試算；ΔCFR 為百分點。
function PotentialCard({ potential, contributions }) {
  const byKey = Object.fromEntries(contributions.map((c) => [c.key, c]))
  const rows = [...potential].sort((x, y) => (y.best?.deltaCfr ?? -1) - (x.best?.deltaCfr ?? -1))
  const [open, setOpen] = useState(rows[0]?.key)
  const max = Math.max(...rows.map((p) => p.best?.deltaCfr ?? 0), 1e-9)
  return (
    <section className="card">
      <h2>改善潛力</h2>
      <p className="muted small">每個構件換成同類別最好的選項時，減碳率可提升多少（其他構件不變）。點一列看全部選項。</p>
      <ul className="hbars" aria-label="各構件最佳替換可提升的減碳率">
        {rows.map((p) => {
          const c = byKey[p.key]
          return (
            <li key={p.key} className="hbar-item">
              <button type="button" className={`hbar-row ${open === p.key ? 'is-open' : ''}`} onClick={() => setOpen(p.key)}>
                <span className="hbar-label">{c?.categoryName}<span className="muted small"> {p.blccId}</span></span>
                <span className="hbar-track">
                  {p.best && <span className="hbar-fill" style={{ width: `${(p.best.deltaCfr / max) * 100}%` }} />}
                </span>
                <span className="hbar-value">
                  {p.best ? `+${pts(p.best.deltaCfr)}` : '已是最低碳'}
                  {p.needsRuleCheck && <span className="status-tag status-approx" title="帷幕牆的基準案是否保留外裝，待確認手冊式 11">規則待確認</span>}
                </span>
              </button>
            </li>
          )
        })}
      </ul>
      {rows.filter((p) => p.key === open).map((p) => (
        <div className="table-scroll" key={p.key}>
          <table className="data-table compact">
            <caption className="muted small" style={{ textAlign: 'left' }}>
              {byKey[p.key]?.blccId} {byKey[p.key]?.name} 換成…（依減碳率排序，前 {p.options.length} 名）
            </caption>
            <thead><tr><th>選項</th><th className="num">CFR</th><th className="num">ΔCFR</th><th className="num">ΔEEC kgCO2e</th><th>等級</th></tr></thead>
            <tbody>
              {p.options.map((o) => (
                <tr key={o.blccId}>
                  <td>{o.blccId} {o.name}</td>
                  <td className="num">{pct(o.cfr)}</td>
                  <td className={`num ${o.deltaCfr > 0 ? 'delta-good' : o.deltaCfr < 0 ? 'delta-bad' : ''}`}>{signed(o.deltaCfr)}</td>
                  <td className="num">{o.deltaEec > 0 ? '+' : ''}{kg(o.deltaEec)}</td>
                  <td><GradeBadge grade={o.grade} size="sm" /></td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      ))}
    </section>
  )
}

const STATUS = { proposed: ['建議', 'status-approx'], chosen: ['已選定', 'status-guess'], applied: ['已套用', 'status-ok'] }

// 套用過的建議都留著 applied 狀態；只有最近一次套用的才是模型目前的樣子，其他標「曾套用」
function statusLabel(p, latestAppliedId) {
  if (p.status === 'applied' && p.id !== latestAppliedId) return ['曾套用', '']
  if (p.status === 'applied') return ['已套用（目前）', 'status-ok']
  return STATUS[p.status] ?? STATUS.proposed
}

function ProposalsCard({ project, current }) {
  const [list, setList] = useState(null)
  const [error, setError] = useState('')
  const load = useCallback(() => {
    if (!project) return
    listProposals(project).then(setList).catch((e) => setError(e.message))
  }, [project])
  useEffect(() => { load() }, [load])

  const set = async (id, status) => {
    setError('')
    try { await setProposalStatus(id, status); load() } catch (e) { setError(e.message) }
  }

  // 刪除只移除建議紀錄，已寫回模型的參數不會還原，確認訊息要講清楚
  const remove = async (p) => {
    const note = p.appliedAt ? '\n這份建議已寫回過模型；刪除只移除紀錄，模型裡的 LEBR_BlccId 不會還原。' : ''
    if (!window.confirm(`刪除建議「${p.title}」？刪除後無法復原。${note}`)) return
    setError('')
    try { await deleteProposal(p.id); load() } catch (e) { setError(e.message) }
  }

  const latestAppliedId = list
    ?.filter((p) => p.appliedAt)
    .reduce((a, b) => (!a || b.appliedAt > a.appliedAt ? b : a), null)?.id
  const chosen = list?.filter((p) => p.status === 'chosen') ?? []
  const chosenIds = new Set(chosen.map((p) => p.id))

  return (
    <section className="card">
      <div className="card-head">
        <div>
          <h2>減碳建議</h2>
          <p className="muted small">
            由 AI 依上面的分析提出，數字用 lebr-snapshot scenario 試算（不是 AI 自己算的）。可以選定多份互不衝突的建議，
            在 Revit 按「LEBR → 套用建議」一次寫回模型。
          </p>
        </div>
        <button type="button" onClick={load}>重新整理</button>
      </div>
      {error && <div className="banner banner-error">{error}</div>}
      {chosen.length > 0 && (
        <div className="banner banner-info small">
          已選定 {chosen.length} 份，等待在 Revit 按「套用建議」。
          {chosen.length > 1 && ' 每份的試算是單獨算的，合併後的減碳率以套用後重算為準。'}
        </div>
      )}
      {list?.length === 0 && (
        <p className="muted small">「{project}」還沒有建議。請 AI agent 依 MaterialMapping/AGENTS.md 的「減碳建議」流程產生 proposals.json。</p>
      )}
      <ul className="proposal-list">
        {list?.map((p) => {
          const [label, tone] = statusLabel(p, latestAppliedId)
          const result = p.applied ?? p.expected
          // 和已選定的建議衝突就不能選（後端也會擋），列出原因
          const blockers = p.status === 'chosen' ? [] : (p.conflicts ?? []).filter((c) => chosenIds.has(c.id))
          return (
            <li key={p.id} className={`proposal ${p.status === 'chosen' ? 'is-chosen' : ''}`}>
              <div className="proposal-head">
                <span className={`status-tag ${tone}`}>{label}</span>
                <strong>{p.title}</strong>
                {result && (
                  <span className="proposal-result">
                    {p.applied ? '套用後' : '試算'} {pct(result.cfr)}
                    <span className={result.cfr >= current.cfr ? 'delta-good' : 'delta-bad'}>（{signed(result.cfr - current.cfr)}）</span>
                    <GradeBadge grade={result.grade} size="sm" />
                  </span>
                )}
              </div>
              <ul className="change-list small">
                {p.changes.map((c, i) => (
                  <li key={i}>
                    {c.typeName ? `「${c.typeName}」` : '所有類型'}：<code>{c.from}</code> → <code>{c.to}</code>
                    {c.dropFinish && '，拿掉外裝'}
                  </li>
                ))}
              </ul>
              {p.reason && <p className="small muted">{p.reason}</p>}
              <div className="proposal-actions">
                {p.status === 'chosen' ? (
                  <button type="button" onClick={() => set(p.id, 'proposed')}>取消選定</button>
                ) : (
                  <button type="button" onClick={() => set(p.id, 'chosen')} disabled={blockers.length > 0}
                    title={p.appliedAt ? '選定後在 Revit 按「套用建議」，會把這份的變更再寫一次到目前的模型' : undefined}>
                    {p.appliedAt ? '再次選定' : '選定這份'}
                  </button>
                )}
                {p.appliedAt && <span className="small muted">已於 {time(p.appliedAt)} 寫回模型</span>}
                {blockers.length > 0 && (
                  <span className="small delta-bad">
                    和已選定的{blockers.map((c) => `「${c.title}」`).join('、')}衝突（{blockers[0].reason}），要先取消那一份
                  </span>
                )}
                <button type="button" className="danger-link proposal-delete" aria-label={`刪除建議 ${p.title}`}
                  onClick={() => remove(p)}>刪除</button>
              </div>
            </li>
          )
        })}
      </ul>
    </section>
  )
}

// CFR 差距以百分點表示（0.0195 → 1.95 個百分點）
const pts = (v) => `${(v * 100).toFixed(2)} 個百分點`
const signed = (v) => `${v > 0 ? '+' : ''}${(v * 100).toFixed(2)} pt`
