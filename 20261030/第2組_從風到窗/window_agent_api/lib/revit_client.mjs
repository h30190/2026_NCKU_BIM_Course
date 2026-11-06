// Revit MCP 外掛的 WebSocket 用戶端(與 REVIT_MCP_study 的 MCP-Server/src/socket.ts 同一套協定)。
// 使用 Node 內建 WebSocket(Node 22+),不需安裝套件。
export const DEFAULT_PORT = Number(process.env.REVIT_MCP_PORT || 8964);

export async function connect({ port = DEFAULT_PORT, client = 'window-agent-api', timeoutMs = 60000 } = {}) {
  const ws = new WebSocket(`ws://localhost:${port}/?client=${encodeURIComponent(client)}`);
  const pending = new Map();
  await new Promise((resolve, reject) => {
    const fail = () => reject(new Error(`連不上 Revit MCP(port ${port}),請確認 Revit 已開啟且 MCP 外掛服務已啟動`));
    const t = setTimeout(fail, 10000);
    ws.onopen = () => { clearTimeout(t); resolve(); };
    ws.onerror = () => { clearTimeout(t); fail(); };
  });
  ws.onmessage = (ev) => {
    const r = JSON.parse(ev.data.toString());
    const h = pending.get(r.RequestId);
    if (h) { pending.delete(r.RequestId); h(r); }
  };
  let seq = 0;
  // 回傳 { ok, data, error };不丟例外,由呼叫端決定如何處理失敗。
  const call = (tool, params = {}) => new Promise((resolve) => {
    const id = `wa_${Date.now()}_${seq++}`;
    const t = setTimeout(() => { pending.delete(id); resolve({ ok: false, data: null, error: `逾時:${tool}` }); }, timeoutMs);
    pending.set(id, (r) => { clearTimeout(t); resolve({ ok: !!r.Success, data: r.Data, error: r.Error }); });
    ws.send(JSON.stringify({ CommandName: tool, Parameters: params, RequestId: id }));
  });
  return { call, close: () => ws.close() };
}

// 連線、執行 fn、一定關閉
export async function withRevit(fn, opts) {
  const rv = await connect(opts);
  try { return await fn(rv); } finally { rv.close(); }
}

// get_element_info 的 Parameters 陣列 → { 名稱: 值 }(同名取第一個)
export function paramsOf(info) {
  const out = {};
  for (const p of info?.Parameters || []) if (!(p.Name in out)) out[p.Name] = p.Value;
  return out;
}

export const pick = (obj, names) => names.map((n) => obj[n]).find((v) => v != null) ?? null;
