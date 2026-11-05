import { useCallback, useEffect, useMemo, useState } from 'react'
import { getMapping, getBlccOptions, updateMapping } from '../api.js'
import { kg, num } from '../format.js'

// 材料對照（原 MaterialMapping 工具，組員製作）：AI 透過 Revit MCP 辨識牆、窗類型並產生 B-LCC 代碼草稿，
// 在這裡確認或修改；確認後由 AI agent 寫回模型的 LEBR_BlccId，外掛「從模型帶入」即可讀到。
// 流程與判斷規則見 MaterialMapping/AGENTS.md。

const KINDS = [
  ['wall', '外牆'],
  ['glazing', '玻璃帷幕'],
  ['interior', '內牆'],
  ['window', '窗'],
  ['excluded', '排除／不計入'],
]

export default function MaterialMap({ projectTitle }) {
  const [items, setItems] = useState([])
  const [options, setOptions] = useState({ cw: [], ow: [], iw: [], w: [] })
  const [project, setProject] = useState('')
  const [collapsed, setCollapsed] = useState(() => new Set(['excluded']))
  const [error, setError] = useState('')
  const [loaded, setLoaded] = useState(false)

  const reload = useCallback(async () => {
    const data = await getMapping()
    setItems(data.items ?? [])
    return data.items ?? []
  }, [])

  useEffect(() => {
    let alive = true
    Promise.all([reload(), ...['cw', 'ow', 'iw', 'w'].map(getBlccOptions)])
      .then(([list, cw, ow, iw, w]) => {
        if (!alive) return
        setOptions({ cw, ow, iw, w })
        // 預設篩選與左側選取的案子同名的 Revit 專案
        if (projectTitle && list.some((it) => it.project === projectTitle)) setProject(projectTitle)
        setLoaded(true)
      })
      .catch((e) => alive && (setError(e.message), setLoaded(true)))
    return () => { alive = false }
  }, [reload, projectTitle])

  const save = async (item, patch) => {
    setError('')
    try {
      await updateMapping(item.elementId, item.project, patch)
      await reload()
    } catch (e) {
      setError(`更新失敗：${e.message}`)
    }
  }

  const projects = useMemo(() => [...new Set(items.map((it) => it.project).filter(Boolean))], [items])
  const shown = project ? items.filter((it) => it.project === project) : items
  const total = shown.reduce((s, it) => s + (it.subtotal || 0), 0)
  const pending = shown.filter((it) => it.kind !== 'excluded' && it.confidence !== 'confirmed').length
  const groups = KINDS.map(([kind, label]) => {
    const list = shown.filter((it) => it.kind === kind)
    return {
      kind, label, list,
      subtotal: list.reduce((s, it) => s + (it.subtotal || 0), 0),
      pending: list.filter((it) => it.confidence !== 'confirmed').length,
    }
  }).filter((g) => g.list.length > 0)

  const toggle = (kind) => setCollapsed((cur) => {
    const next = new Set(cur)
    if (next.has(kind)) next.delete(kind)
    else next.add(kind)
    return next
  })

  const bodyOptions = (kind) => (kind === 'interior' ? options.iw : kind === 'window' ? options.w : options.cw)

  return (
    <div className="overview">
      <div className="overview-head">
        <div>
          <h1>材料對照</h1>
          <p className="muted small">Revit 類型 → B-LCC 構件代碼。AI 依規則產生草稿，請逐項確認後再寫回模型的 LEBR_BlccId。</p>
        </div>
        {projects.length > 0 && (
          <label className="snapshot-picker">
            <span className="muted small">Revit 專案</span>
            <select value={project} onChange={(e) => setProject(e.target.value)}>
              <option value="">全部</option>
              {projects.map((p) => <option key={p} value={p}>{p}</option>)}
            </select>
          </label>
        )}
      </div>

      {error && <div className="banner banner-error">{error}</div>}

      {!loaded && <p className="muted">載入中…</p>}

      {loaded && items.length > 0 && (
      <section className="card mapping-summary">
        <div>
          <span className="muted small">材料試算合計</span>
          <span className="stat-value">{kg(total)}</span> <span className="muted small">kgCO2e</span>
        </div>
        <div>
          <span className="muted small">待確認</span>
          <span className={`stat-value ${pending > 0 ? 'delta-bad' : ''}`}>{pending}</span> <span className="muted small">項</span>
        </div>
        <p className="muted small mapping-note">
          只算「新建碳排 × 面積」，不含更新碳排與基準案，<b>不是正式 LEBR 評估</b>。正式結果請在 Revit「LEBR 評估」按「從模型帶入」→「計算」。
        </p>
      </section>
      )}

      {loaded && items.length === 0 && !error && (
        <div className="notice">
          <h2>還沒有材料對照資料</h2>
          <p>請 AI agent 依 <code>MaterialMapping/AGENTS.md</code> 透過 Revit MCP 辨識模型，結果會寫進 <code>MaterialMapping/data/mapping.json</code>，存檔後按右上角「重新整理」或切換分頁即可看到。</p>
        </div>
      )}

      {groups.map((g) => {
        const open = !collapsed.has(g.kind)
        return (
          <section key={g.kind} className="card mapping-group">
            <button type="button" className="mapping-group-head" aria-expanded={open} onClick={() => toggle(g.kind)}>
              <span>
                <b>{open ? '▾' : '▸'} {g.label}</b>（{g.list.length} 種類型）
                {g.kind !== 'excluded' && g.pending > 0 && <span className="rank-tag">{g.pending} 項待確認</span>}
              </span>
              <span className="muted">{g.kind === 'excluded' ? '不計入' : `小計 ${kg(g.subtotal)} kgCO2e`}</span>
            </button>
            {open && (
              <div className="table-scroll">
                <table className="data-table">
                  <thead>
                    <tr>
                      {!project && <th>專案</th>}
                      <th>Revit 類型</th>
                      <th>本體</th>
                      <th>外裝</th>
                      <th className="num">面積 m²</th>
                      <th className="num">單位碳排</th>
                      <th className="num">小計</th>
                      <th>狀態</th>
                      <th>備註</th>
                    </tr>
                  </thead>
                  <tbody>
                    {g.list.map((it) => (
                      <tr key={`${it.project}-${it.elementId}`}>
                        {!project && <td className="muted small">{it.project}</td>}
                        <td className="wrap">{it.typeName}</td>
                        <td>
                          {it.kind === 'excluded' ? '—' : (
                            <select value={it.bodyId ?? ''} aria-label={`${it.typeName} 本體代碼`}
                              onChange={(e) => save(it, { bodyId: e.target.value })}>
                              <option value="">（未設定）</option>
                              {bodyOptions(it.kind).map((o) => <option key={o.id} value={o.id}>{o.id}　{o.name}</option>)}
                            </select>
                          )}
                        </td>
                        <td>
                          {it.kind === 'wall' ? (
                            <select value={it.finishId ?? ''} aria-label={`${it.typeName} 外裝代碼`}
                              onChange={(e) => save(it, { finishId: e.target.value })}>
                              <option value="">（未設定）</option>
                              {options.ow.map((o) => <option key={o.id} value={o.id}>{o.id}　{o.name}</option>)}
                            </select>
                          ) : '—'}
                        </td>
                        <td className="num">
                          {it.kind === 'excluded' ? '—' : <AreaInput value={it.areaM2} onCommit={(v) => save(it, { areaM2: v })} />}
                        </td>
                        <td className="num">{it.kind === 'excluded' ? '—' : num(it.unitEC)}</td>
                        <td className="num">{it.kind === 'excluded' ? '—' : kg(it.subtotal)}</td>
                        <td>
                          <span className={`status-tag ${it.confidence === 'confirmed' ? 'status-ok' : 'status-guess'}`}>
                            {it.confidence === 'confirmed' ? '已確認' : 'AI 猜測'}
                          </span>
                          {/近似/.test(it.note ?? '') && <span className="status-tag status-approx">近似值</span>}
                        </td>
                        <td className="wrap muted small">{it.note}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </section>
        )
      })}
    </div>
  )
}

// 面積輸入：離開欄位或按 Enter 才存檔，避免每打一個字就送一次、清空時暫存成 0
function AreaInput({ value, onCommit }) {
  const [text, setText] = useState(String(value ?? 0))
  useEffect(() => { setText(String(value ?? 0)) }, [value])
  const commit = () => {
    const v = Number(text)
    if (text.trim() === '' || !Number.isFinite(v) || v < 0) { setText(String(value ?? 0)); return }
    if (v !== value) onCommit(v)
  }
  return (
    <input type="number" min="0" step="0.01" className="area-input" value={text}
      onChange={(e) => setText(e.target.value)} onBlur={commit}
      onKeyDown={(e) => { if (e.key === 'Enter') e.currentTarget.blur() }} />
  )
}
