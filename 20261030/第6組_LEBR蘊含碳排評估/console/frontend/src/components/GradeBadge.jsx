import { gradeTone } from '../format.js'

// 等級徽章：達到門檻（2 級以上）綠、未達橙；文字本身就是等級，不只靠顏色。
export default function GradeBadge({ grade, size = 'md' }) {
  const tone = gradeTone(grade)
  return (
    <span className={`grade-badge grade-${tone} grade-${size}`} title={tone === 'good' ? '達公有建築 2 級門檻' : tone === 'warn' ? '未達 2 級門檻' : undefined}>
      {grade || '—'}
    </span>
  )
}
