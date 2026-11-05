import { pct, time } from '../format.js'
import GradeBadge from '../components/GradeBadge.jsx'

// W3 案件列表：一個 Revit 模型一個案件（名稱 = 模型檔名），外掛開啟模型時自動登記。
// 已評估的顯示最新等級、CFR、快照數；只登記還沒上傳快照的顯示「尚未評估」。右下角可刪除整個案件。
export default function ProjectList({ projects, selectedId, onSelect, onDelete }) {
  return (
    <nav className="sidebar" aria-label="案件列表">
      <h2 className="sidebar-title">案件</h2>
      {projects.length === 0 && <p className="muted small">尚無資料。在 Revit 開啟模型後按「重新整理」。</p>}
      <ul className="project-list">
        {projects.map((p) => (
          <li key={p.id} className="project-item">
            <button
              type="button"
              className={`project-card ${p.id === selectedId ? 'is-selected' : ''} ${p.latest ? '' : 'is-pending'}`}
              onClick={() => onSelect(p.id)}
              aria-current={p.id === selectedId ? 'true' : undefined}
            >
              <div className="project-card-head">
                <span className="project-title">{p.title}</span>
                {p.latest ? <GradeBadge grade={p.latest.grade} size="sm" /> : <span className="status-tag">尚未評估</span>}
              </div>
              {p.latest ? (
                <>
                  <div className="project-card-meta">
                    <span>CFR {pct(p.latest.cfr)}</span>
                    <span>{p.snapshotCount} 筆快照</span>
                  </div>
                  <div className="muted small">{time(p.latest.createdAt)}</div>
                </>
              ) : (
                <div className="muted small">尚未上傳快照</div>
              )}
            </button>
            {/* 放在卡片按鈕外面（按鈕不能巢狀），用定位疊在右下角 */}
            <button type="button" className="danger-link project-delete"
              aria-label={`刪除案件 ${p.title}`} onClick={() => onDelete(p)}>
              刪除
            </button>
          </li>
        ))}
      </ul>
    </nav>
  )
}
