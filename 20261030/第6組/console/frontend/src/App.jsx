import { useCallback, useEffect, useState } from 'react'
import { health, listProjects, listSnapshots, getSnapshot, compareSnapshots } from './api.js'
import ProjectList from './pages/ProjectList.jsx'
import Overview from './pages/Overview.jsx'
import Timeline, { MAX_COMPARE } from './pages/Timeline.jsx'
import Compare from './pages/Compare.jsx'
import MaterialMap from './pages/MaterialMap.jsx'
import Analysis from './pages/Analysis.jsx'

// 依工作流程排列：先對照材料（寫入 LEBR_BlccId）→ 外掛計算上傳 → 看結果與建議（選定後回 Revit 套用）→ 比較
const VIEWS = [
  ['mapping', '材料對照'],
  ['overview', '評估總覽'],
  ['analysis', '分析與建議'],
  ['timeline', '快照時間軸'],
  ['compare', '方案比較'],
]

// 單頁應用：左側案子清單，右側材料對照／評估總覽／快照時間軸／方案比較（控制台計畫書第 6 節）。
export default function App() {
  const [online, setOnline] = useState(null) // null = 檢查中
  const [projects, setProjects] = useState([])
  const [projectId, setProjectId] = useState(null)
  const [snapshots, setSnapshots] = useState([])
  const [snapshotId, setSnapshotId] = useState(null)
  const [snapshot, setSnapshot] = useState(null)
  const [error, setError] = useState('')
  const [view, setView] = useState('overview')
  const [selected, setSelected] = useState([]) // 勾選要比較的快照 ID
  const [compare, setCompare] = useState(null)

  const refresh = useCallback(async () => {
    setError('')
    const ok = await health()
    setOnline(ok)
    if (!ok) return
    try {
      const list = await listProjects()
      setProjects(list)
      setProjectId((cur) => (list.some((p) => p.id === cur) ? cur : list[0]?.id ?? null))
    } catch (e) {
      setError(e.message)
    }
  }, [])

  useEffect(() => { refresh() }, [refresh])

  // 換案子：清掉比較選取，回到總覽（材料對照不受案子影響，留在原頁）
  useEffect(() => {
    setSelected([])
    setCompare(null)
    setView((v) => (v === 'mapping' ? v : 'overview'))
  }, [projectId])

  // 載入快照清單，預設選最新一筆
  useEffect(() => {
    if (projectId == null) { setSnapshots([]); setSnapshotId(null); return }
    let alive = true
    listSnapshots(projectId)
      .then((list) => { if (alive) { setSnapshots(list); setSnapshotId(list[0]?.id ?? null) } })
      .catch((e) => alive && setError(e.message))
    return () => { alive = false }
  }, [projectId, projects])

  useEffect(() => {
    if (snapshotId == null) { setSnapshot(null); return }
    let alive = true
    getSnapshot(snapshotId)
      .then((d) => alive && setSnapshot(d))
      .catch((e) => alive && setError(e.message))
    return () => { alive = false }
  }, [snapshotId])

  const toggle = (id) =>
    setSelected((cur) => (cur.includes(id) ? cur.filter((x) => x !== id) : cur.length < MAX_COMPARE ? [...cur, id] : cur))

  // 由舊到新排列，快照 1 是最早的那筆（比較基準）
  const runCompare = async () => {
    setError('')
    try {
      setCompare(await compareSnapshots([...selected].sort((a, b) => a - b)))
      setView('compare')
    } catch (e) {
      setError(e.message)
    }
  }

  const openSnapshot = (id) => {
    setSnapshotId(id)
    setView('overview')
  }

  return (
    <div className="app">
      <header className="topbar">
        <div className="brand">
          <strong>LebrCarbon 控制台</strong>
          <span className="muted">低碳（低蘊含碳）建築評估 LEBR-2025</span>
        </div>
        <div className="topbar-right">
          <span className={`conn conn-${online === null ? 'wait' : online ? 'ok' : 'off'}`}>
            {online === null ? '連線中…' : online ? '後端已連線' : '後端未連線'}
          </span>
          <button type="button" onClick={refresh}>重新整理</button>
        </div>
      </header>

      {online === false ? (
        <Offline />
      ) : (
        <div className="layout">
          <ProjectList projects={projects} selectedId={projectId} onSelect={setProjectId} />
          <main className="content">
            {error && <div className="banner banner-error">{error}</div>}
            {online && (
              <>
                <nav className="tabs" aria-label="檢視">
                  {VIEWS.map(([key, label]) => (
                    <button key={key} type="button" aria-current={view === key ? 'page' : undefined}
                      className={view === key ? 'is-active' : ''}
                      disabled={key === 'compare' && !compare}
                      title={key === 'compare' && !compare ? '先在快照時間軸勾選 2～3 筆' : undefined}
                      onClick={() => setView(key)}>
                      {label}{key === 'timeline' ? `（${snapshots.length}）` : ''}
                    </button>
                  ))}
                </nav>
                {view === 'mapping' && (
                  <MaterialMap projectTitle={projects.find((p) => p.id === projectId)?.title} />
                )}
                {view !== 'mapping' && projects.length === 0 && <Empty />}
                {view === 'overview' && projects.length > 0 && (
                  <Overview snapshot={snapshot} snapshots={snapshots} snapshotId={snapshotId} onSelectSnapshot={setSnapshotId} />
                )}
                {view === 'analysis' && projects.length > 0 && <Analysis snapshot={snapshot} />}
                {view === 'timeline' && projects.length > 0 && (
                  <Timeline snapshots={snapshots} selected={selected} onToggle={toggle} onOpen={openSnapshot} onCompare={runCompare} />
                )}
                {view === 'compare' && <Compare data={compare} onBack={() => setView('timeline')} />}
              </>
            )}
          </main>
        </div>
      )}

      <footer className="footer">設計自主檢討用，非正式標示結果。正式標示須經評定專業機構審查。</footer>
    </div>
  )
}

function Offline() {
  return (
    <div className="notice">
      <h2>控制台後端沒有運行</h2>
      <p>請在 <code>20261030/第6組/console</code> 資料夾執行：</p>
      <pre>.\start.ps1</pre>
      <p className="muted">會開兩個視窗（後端 :8080、前端 :5173）。後端視窗出現 listening 後按右上角「重新整理」。</p>
    </div>
  )
}

function Empty() {
  return (
    <div className="notice">
      <h2>還沒有任何評估快照</h2>
      <p>兩種方式加入第一筆：</p>
      <ol>
        <li>在 Revit「LEBR 評估」視窗按「計算」，再按「上傳到控制台」。</li>
        <li>不開 Revit，用評估輸入檔產生快照（需要 .NET SDK）：
          <pre>dotnet run --project LebrCarbon\src\LebrCarbon.Cli -- 專案1.lebr.json --upload</pre>
        </li>
      </ol>
    </div>
  )
}
