import { pct, time } from '../format.js'
import GradeBadge from '../components/GradeBadge.jsx'

// W3 案子列表：案子名稱、最新等級、CFR、快照數、更新時間。
export default function ProjectList({ projects, selectedId, onSelect }) {
  return (
    <nav className="sidebar" aria-label="案子列表">
      <h2 className="sidebar-title">案子</h2>
      {projects.length === 0 && <p className="muted small">尚無資料</p>}
      <ul className="project-list">
        {projects.map((p) => (
          <li key={p.id}>
            <button
              type="button"
              className={`project-card ${p.id === selectedId ? 'is-selected' : ''}`}
              onClick={() => onSelect(p.id)}
              aria-current={p.id === selectedId ? 'true' : undefined}
            >
              <div className="project-card-head">
                <span className="project-title">{p.title}</span>
                <GradeBadge grade={p.latest?.grade} size="sm" />
              </div>
              <div className="project-card-meta">
                <span>CFR {pct(p.latest?.cfr)}</span>
                <span>{p.snapshotCount} 筆快照</span>
              </div>
              <div className="muted small">{time(p.latest?.createdAt)}</div>
            </button>
          </li>
        ))}
      </ul>
    </nav>
  )
}
