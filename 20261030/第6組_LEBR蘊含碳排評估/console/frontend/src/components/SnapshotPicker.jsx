import { time } from '../format.js'

// 快照切換：評估總覽與分析與建議共用；選取狀態放在 App，切換分頁時保留同一筆。
export default function SnapshotPicker({ snapshots, snapshotId, onSelect }) {
  return (
    <label className="snapshot-picker">
      <span className="muted small">快照</span>
      <select value={snapshotId ?? ''} onChange={(e) => onSelect(Number(e.target.value))}>
        {snapshots.map((s) => (
          <option key={s.id} value={s.id}>{s.name}（{s.grade}，{time(s.createdAt)}）</option>
        ))}
      </select>
    </label>
  )
}
