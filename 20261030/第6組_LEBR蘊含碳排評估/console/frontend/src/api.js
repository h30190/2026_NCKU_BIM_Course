// 所有 fetch 集中在這裡（與 RFI 範例相同），頁面元件不直接寫網址。
// 後端位址：Vite proxy 下用相對路徑即可；直連後端時改這裡。
export const API_BASE = ''

async function request(path, init) {
  const r = await fetch(`${API_BASE}${path}`, init)
  if (!r.ok) {
    let msg = await r.text()
    try { msg = JSON.parse(msg).error ?? msg } catch { /* 不是 JSON 就用原文 */ }
    throw new Error(msg)
  }
  return r.json()
}

const get = (path) => request(path)

export async function health() {
  try {
    const r = await fetch(`${API_BASE}/api/health`)
    return r.ok
  } catch {
    return false
  }
}

export const listProjects = () => get('/api/projects')
export const listSnapshots = (projectId) => get(`/api/projects/${projectId}/snapshots`)
export const getSnapshot = (id) => get(`/api/snapshots/${id}`)
export const compareSnapshots = (ids) => get(`/api/compare?ids=${ids.join(',')}`)
export const deleteProject = (id) => request(`/api/projects/${id}`, { method: 'DELETE' })
// 刪快照不會刪案件；刪光後案件回到「尚未評估」
export const deleteSnapshot = (id) => request(`/api/snapshots/${id}`, { method: 'DELETE' })
// Excel 計算書直接用連結下載，不經 fetch
export const excelUrl = (id) => `${API_BASE}/api/snapshots/${id}/excel`

// 材料對照（原 MaterialMapping 工具）
export const getMapping = () => get('/api/mapping')
export const getBlccOptions = (category) => get(`/api/blcc?category=${encodeURIComponent(category)}`)
export const updateMapping = (elementId, project, patch) =>
  request(`/api/mapping/${elementId}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ ...patch, project }),
  })

// 減碳建議（AI 分析計畫書 6 節）：AI 寫入、網頁選定、外掛套用後回報
export const listProposals = (project) => get(`/api/proposals?project=${encodeURIComponent(project)}`)
export const setProposalStatus = (id, status) =>
  request(`/api/proposals/${encodeURIComponent(id)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ status }),
  })
// 刪除只移除建議紀錄；已寫回模型的參數不會還原
export const deleteProposal = (id) => request(`/api/proposals/${encodeURIComponent(id)}`, { method: 'DELETE' })
