// 後端位址：Vite proxy 下用相對路徑即可；直連後端時改這裡。
export const API_BASE = ''

function authHeader() {
  const t = localStorage.getItem('rfi_token')
  return t ? { Authorization: `Bearer ${t}` } : {}
}

export async function login(username, password) {
  const r = await fetch(`${API_BASE}/api/login`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ username, password }),
  })
  if (!r.ok) throw new Error(await r.text())
  const data = await r.json()
  localStorage.setItem('rfi_token', data.token)
  localStorage.setItem('rfi_user', JSON.stringify({ username: data.username, role: data.role }))
  return data
}

export function logout() {
  fetch(`${API_BASE}/api/logout`, { method: 'POST', headers: authHeader() }).catch(() => {})
  localStorage.removeItem('rfi_token')
  localStorage.removeItem('rfi_user')
}

export function currentUser() {
  try {
    return JSON.parse(localStorage.getItem('rfi_user'))
  } catch {
    return null
  }
}

export async function listUsers() {
  const r = await fetch(`${API_BASE}/api/users`, { headers: authHeader() })
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}

export async function createUser(username, password, role) {
  const r = await fetch(`${API_BASE}/api/users`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', ...authHeader() },
    body: JSON.stringify({ username, password, role }),
  })
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}

export async function patchUser(username, body) {
  const r = await fetch(`${API_BASE}/api/users/${encodeURIComponent(username)}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...authHeader() },
    body: JSON.stringify(body),
  })
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}

export async function listIssues(status = '', archived = '') {
  const p = new URLSearchParams()
  if (status) p.set('status', status)
  if (archived) p.set('archived', archived) // '1' 只看已封存，'all' 全看，預設只看未封存
  const q = p.toString() ? `?${p.toString()}` : ''
  const r = await fetch(`${API_BASE}/api/issues${q}`)
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}

export async function getIssue(id) {
  const r = await fetch(`${API_BASE}/api/issues/${id}`)
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}

export async function patchIssue(id, body) {
  const r = await fetch(`${API_BASE}/api/issues/${id}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json', ...authHeader() },
    body: JSON.stringify(body),
  })
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}

export async function createIssue(formData) {
  const r = await fetch(`${API_BASE}/api/issues`, {
    method: 'POST',
    headers: authHeader(),
    body: formData,
  })
  if (!r.ok) throw new Error(await r.text())
  return r.json()
}
