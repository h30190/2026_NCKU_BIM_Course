import { useEffect, useState } from 'react'
import { login, logout, currentUser, listUsers, createUser, patchUser, listIssues, getIssue, patchIssue } from './api.js'

// Procore 風格 RFI 圖台（真版：接後端 API）。
// 權限：viewer 唯讀；member 可寫；admin 多「使用者管理」分頁（後端也擋）。

const STATUS_META = {
  open: { label: '待處理', bg: '#fff3e0', fg: '#e65100' },
  in_progress: { label: '處理中', bg: '#e3f2fd', fg: '#0d47a1' },
  closed: { label: '已關閉', bg: '#e8f5e9', fg: '#1b5e20' },
}

function Pill({ status }) {
  const m = STATUS_META[status] || { label: status, bg: '#eee', fg: '#333' }
  return (
    <span style={{ background: m.bg, color: m.fg, borderRadius: 12, padding: '2px 10px', fontSize: 12, fontWeight: 'bold', whiteSpace: 'nowrap' }}>
      {m.label}
    </span>
  )
}

export default function Procore() {
  const [me, setMe] = useState(() => currentUser())
  const [loginForm, setLoginForm] = useState({ username: '', password: '' })
  const [loginErr, setLoginErr] = useState('')
  const [tab, setTab] = useState('issues') // issues | archived | users
  const [showFilter, setShowFilter] = useState(true)
  const [q, setQ] = useState('')
  const [fStatus, setFStatus] = useState('')
  const [fModel, setFModel] = useState('')
  const [fAssignee, setFAssignee] = useState('')
  const [issues, setIssues] = useState([])
  const [users, setUsers] = useState([])
  const [detailId, setDetailId] = useState(null)
  const [detail, setDetail] = useState(null)
  const [err, setErr] = useState('')
  const [newUser, setNewUser] = useState({ username: '', password: '', role: 'member' })

  const role = me?.role
  const isAdmin = role === 'admin'
  const canWrite = !!me && role !== 'viewer'

  async function refresh() {
    try {
      setErr('')
      const data = await listIssues(fStatus, tab === 'archived' ? '1' : '')
      setIssues(data)
    } catch (e) {
      setErr('後端連不上（先 go run ./backend）：' + (e.message || e))
    }
  }

  async function refreshUsers() {
    if (!isAdmin) return
    try {
      setUsers(await listUsers())
    } catch (e) {
      setErr('讀使用者失敗：' + (e.message || e))
    }
  }

  useEffect(() => { refresh() }, [fStatus, tab])
  useEffect(() => { refreshUsers() }, [me])
  useEffect(() => {
    if (detailId == null) { setDetail(null); return }
    getIssue(detailId).then(setDetail).catch((e) => setErr(String(e.message || e)))
  }, [detailId])

  async function doLogin(e) {
    e.preventDefault()
    try {
      setLoginErr('')
      const data = await login(loginForm.username, loginForm.password)
      setMe({ username: data.username, role: data.role })
    } catch {
      setLoginErr('帳號或密碼錯誤（預設 admin / changeme）')
    }
  }

  function doLogout() {
    logout()
    setMe(null)
    setDetailId(null)
  }

  async function mutate(fn) {
    try {
      await fn()
      refresh()
      if (detailId != null) setDetail(await getIssue(detailId))
    } catch (e) {
      setErr('寫入失敗（權限？登入？）：' + (e.message || e))
    }
  }

  if (!me) {
    return (
      <div style={{ fontFamily: 'system-ui', display: 'flex', justifyContent: 'center', paddingTop: 120 }}>
        <form onSubmit={doLogin} style={{ border: '1px solid #ddd', padding: 32, width: 320, background: '#fff' }}>
          <h2>RFI 圖台登入</h2>
          <input value={loginForm.username} onChange={(e) => setLoginForm({ ...loginForm, username: e.target.value })}
            placeholder="帳號" style={{ width: '100%', padding: 8, marginBottom: 8, boxSizing: 'border-box' }} />
          <input type="password" value={loginForm.password} onChange={(e) => setLoginForm({ ...loginForm, password: e.target.value })}
            placeholder="密碼" style={{ width: '100%', padding: 8, marginBottom: 12, boxSizing: 'border-box' }} />
          {loginErr && <p style={{ color: 'crimson', fontSize: 13 }}>{loginErr}</p>}
          <button type="submit" style={{ width: '100%', padding: 8 }}>登入</button>
          <p style={{ color: '#999', fontSize: 12 }}>未登入只能看列表（課堂投影用），寫入要登入。預設 admin / changeme。</p>
        </form>
      </div>
    )
  }

  const models = [...new Set(issues.map((i) => i.model_name || '未分類'))]
  let rows = issues
  if (q) rows = rows.filter((i) => (i.title + (i.description || '')).includes(q))
  if (fModel) rows = rows.filter((i) => (i.model_name || '未分類') === fModel)
  if (fAssignee) rows = rows.filter((i) => i.assignee === fAssignee)

  return (
    <div style={{ fontFamily: 'system-ui', background: '#f5f6f8', minHeight: '100vh' }}>
      <div style={{ background: '#fff', borderBottom: '1px solid #ddd', padding: '12px 24px' }}>
        <div style={{ color: '#666', fontSize: 13 }}>Project Tools ＞ RFIs（sample）</div>
        <div style={{ display: 'flex', alignItems: 'center', gap: 16 }}>
          <h2 style={{ margin: '4px 0' }}>RFIs</h2>
          <div style={{ display: 'flex', gap: 4, marginLeft: 12 }}>
            {[['issues', '議題'], ['archived', '已封存'], ...(isAdmin ? [['users', '使用者管理']] : [])].map(([k, label]) => (
              <button key={k} onClick={() => { setTab(k); setDetailId(null) }}
                style={{ border: 'none', background: 'none', cursor: 'pointer', padding: '8px 12px',
                  borderBottom: tab === k ? '3px solid #0b5ed7' : '3px solid transparent',
                  fontWeight: tab === k ? 'bold' : 'normal' }}>{label}</button>
            ))}
          </div>
          <div style={{ marginLeft: 'auto', display: 'flex', gap: 8, alignItems: 'center', fontSize: 13 }}>
            <span>{me.username}（{role}）</span>
            <details style={{ position: 'relative' }}>
              <summary style={{ cursor: 'pointer', color: '#0b5ed7' }}>token</summary>
              <div style={{ position: 'absolute', right: 0, background: '#fff', border: '1px solid #ddd', padding: 8, zIndex: 10, width: 300, wordBreak: 'break-all' }}>
                外掛貼這串：<code style={{ fontSize: 11 }}>{localStorage.getItem('rfi_token')}</code>
              </div>
            </details>
            <button onClick={doLogout}>登出</button>
          </div>
        </div>
      </div>

      {tab === 'users' ? (
        <div style={{ padding: 24, maxWidth: 800 }}>
          <h3>使用者管理</h3>
          {err && <p style={{ color: 'crimson' }}>{err}</p>}
          <table style={{ width: '100%', background: '#fff', borderCollapse: 'collapse' }}>
            <thead><tr style={{ background: '#fafafa' }}>
              {['帳號', '角色', '狀態', '操作'].map((h) => (
                <th key={h} style={{ textAlign: 'left', padding: 10, borderBottom: '1px solid #ddd' }}>{h}</th>
              ))}
            </tr></thead>
            <tbody>
              {users.map((u) => (
                <tr key={u.username}>
                  <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{u.username}</td>
                  <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>
                    <select value={u.role} disabled={u.username === me.username}
                      onChange={(e) => mutate(() => patchUser(u.username, { role: e.target.value }).then(refreshUsers))}>
                      <option value="admin">admin</option>
                      <option value="member">member</option>
                      <option value="viewer">viewer</option>
                    </select>
                  </td>
                  <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{u.active ? '啟用' : '停用'}</td>
                  <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>
                    <button disabled={u.username === me.username}
                      onClick={() => mutate(() => patchUser(u.username, { active: !u.active }).then(refreshUsers))}>
                      {u.active ? '停用' : '啟用'}
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
          <div style={{ marginTop: 16, display: 'flex', gap: 8 }}>
            <input placeholder="新帳號" value={newUser.username}
              onChange={(e) => setNewUser({ ...newUser, username: e.target.value })} style={{ padding: 6 }} />
            <input type="password" placeholder="密碼" value={newUser.password}
              onChange={(e) => setNewUser({ ...newUser, password: e.target.value })} style={{ padding: 6 }} />
            <select value={newUser.role} onChange={(e) => setNewUser({ ...newUser, role: e.target.value })}>
              <option>member</option><option>viewer</option><option>admin</option>
            </select>
            <button onClick={() => mutate(() => createUser(newUser.username, newUser.password, newUser.role)
              .then(() => { setNewUser({ username: '', password: '', role: 'member' }); return refreshUsers() }))}>
              新增
            </button>
          </div>
        </div>
      ) : (
        <div style={{ padding: 16 }}>
          <div style={{ display: 'flex', gap: 8, marginBottom: 12 }}>
            <input value={q} onChange={(e) => setQ(e.target.value)} placeholder="搜尋標題"
              style={{ padding: 8, width: 260 }} />
            <button onClick={() => setShowFilter(!showFilter)}>☰ 篩選</button>
            {!canWrite && <span style={{ marginLeft: 'auto', color: '#999', fontSize: 13 }}>viewer 唯讀</span>}
          </div>
          {err && <p style={{ color: 'crimson' }}>{err}</p>}
          <div style={{ display: 'flex', gap: 16, alignItems: 'flex-start' }}>
            {showFilter && (
              <div style={{ width: 230, background: '#fff', border: '1px solid #ddd', padding: 16, flexShrink: 0 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between', marginBottom: 12 }}>
                  <b>篩選</b>
                  <button onClick={() => { setFStatus(''); setFModel(''); setFAssignee(''); setQ('') }}
                    style={{ border: 'none', background: 'none', color: '#0b5ed7', cursor: 'pointer' }}>清除</button>
                </div>
                <div style={{ fontSize: 13, color: '#666', marginBottom: 4 }}>狀態</div>
                <select value={fStatus} onChange={(e) => setFStatus(e.target.value)} style={{ width: '100%', padding: 6, marginBottom: 12 }}>
                  <option value="">全部</option>
                  <option value="open">待處理</option>
                  <option value="in_progress">處理中</option>
                  <option value="closed">已關閉</option>
                </select>
                <div style={{ fontSize: 13, color: '#666', marginBottom: 4 }}>檔案/模型</div>
                <select value={fModel} onChange={(e) => setFModel(e.target.value)} style={{ width: '100%', padding: 6, marginBottom: 12 }}>
                  <option value="">全部</option>
                  {models.map((m) => <option key={m} value={m}>{m}</option>)}
                </select>
                <div style={{ fontSize: 13, color: '#666', marginBottom: 4 }}>指派給</div>
                <select value={fAssignee} onChange={(e) => setFAssignee(e.target.value)} style={{ width: '100%', padding: 6 }}>
                  <option value="">全部</option>
                  {users.map((u) => <option key={u.username} value={u.username}>{u.username}</option>)}
                </select>
              </div>
            )}
            <div style={{ flex: 1, background: '#fff', border: '1px solid #ddd', overflowX: 'auto' }}>
              <table style={{ width: '100%', borderCollapse: 'collapse', fontSize: 14 }}>
                <thead><tr style={{ background: '#fafafa' }}>
                  {['#', '標題', '狀態', '檔案', '視圖', '回報', '指派', '更新'].map((h) => (
                    <th key={h} style={{ textAlign: 'left', padding: 10, borderBottom: '1px solid #ddd', whiteSpace: 'nowrap' }}>{h}</th>
                  ))}
                </tr></thead>
                <tbody>
                  {rows.map((it) => (
                    <tr key={it.id} style={{ background: detailId === it.id ? '#eef4ff' : '#fff' }}>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{it.id}</td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>
                        <a href="#" onClick={(e) => { e.preventDefault(); setDetailId(it.id) }}
                          style={{ color: '#0b5ed7' }}>{it.title}</a>
                      </td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}><Pill status={it.status} /></td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{it.model_name || '未分類'}</td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{it.view_name || '—'}</td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{it.reporter}</td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee' }}>{it.assignee || '—'}</td>
                      <td style={{ padding: 10, borderBottom: '1px solid #eee', whiteSpace: 'nowrap' }}>{it.updated_at}</td>
                    </tr>
                  ))}
                  {rows.length === 0 && (
                    <tr><td colSpan={8} style={{ padding: 24, textAlign: 'center', color: '#999' }}>沒有議題</td></tr>
                  )}
                </tbody>
              </table>
              <div style={{ padding: '8px 16px', color: '#999', fontSize: 13, textAlign: 'right' }}>共 {rows.length} 筆</div>
            </div>
            {detail && (
              <div style={{ width: 340, background: '#fff', border: '1px solid #ddd', padding: 16, flexShrink: 0 }}>
                <div style={{ display: 'flex', justifyContent: 'space-between' }}>
                  <b>#{detail.id} {detail.title}</b>
                  <button onClick={() => setDetailId(null)} style={{ border: 'none', background: 'none', cursor: 'pointer' }}>✕</button>
                </div>
                <p style={{ color: '#666', fontSize: 13 }}>狀態 <Pill status={detail.status} /></p>
                <p style={{ fontSize: 13 }}>檔案：{detail.model_name || '未分類'}　視圖：{detail.view_name || '—'}</p>
                <p style={{ fontSize: 13 }}>回報：{detail.reporter}</p>
                <p style={{ fontSize: 13 }}>指派：
                  {canWrite ? (
                    <select value={detail.assignee || ''}
                      onChange={(e) => mutate(() => patchIssue(detail.id, { assignee: e.target.value }))}>
                      <option value="">未指派</option>
                      {users.map((u) => <option key={u.username} value={u.username}>{u.username}</option>)}
                    </select>
                  ) : (detail.assignee || '—')}
                </p>
                {detail.screenshot && (
                  <img src={`/screenshots/${detail.screenshot}`} alt="issue screenshot"
                    style={{ maxWidth: '100%', border: '1px solid #ccc' }} />
                )}
                {canWrite ? (
                  <div style={{ display: 'flex', gap: 8, marginTop: 12, flexWrap: 'wrap' }}>
                    <button onClick={() => mutate(() => patchIssue(detail.id, { status: 'open' }))}>退回待處理</button>
                    <button onClick={() => mutate(() => patchIssue(detail.id, { status: 'in_progress' }))}>設處理中</button>
                    <button onClick={() => mutate(() => patchIssue(detail.id, { status: 'closed' }))}>設已關閉</button>
                    <button onClick={() => mutate(() => patchIssue(detail.id, { archived: !detail.archived }))}>
                      {detail.archived ? '取消封存' : '封存'}
                    </button>
                  </div>
                ) : (
                  <p style={{ color: '#999', fontSize: 13 }}>viewer 唯讀，不能操作</p>
                )}
              </div>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
