// LEBR 碳排圖台 Mockup：主結構（BIM 模型估算）
// 資料格式見 mockup/DATA_FORMAT.md（format = "lebr-structure-result"）。
// 進資料的三個入口：網址 #d=（Revit「在圖台開啟」）、載入 JSON 檔、拖放檔案。

const FORMAT = "lebr-structure-result";
const CAT = { column: "柱", beam: "梁", slab: "樓板", wall: "結構牆", stair: "樓梯", rebar: "鋼筋" };
const ZONE = { above: "地上層", below: "地下層" };
const STAGES = [["A1 原料開採", "#5ac8fa"], ["A2 原料運輸", "#34c759"], ["A3 產品生產", "#ffb340"], ["A4 成品運輸", "#ff7a59"], ["未分段", "#7c7c82"]];
const LEVEL = { error: ["錯誤", "red"], warning: ["警告", "amber"], info: ["資訊", "blue"] };
const CHECK_TOL = 0.005; // 圖台驗算與外掛總數的容許誤差

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);

const state = { data: null, calc: null, source: "", unit: "kg", zone: "above", sel: null, q: "", fz: "all", fc: "all", level: "all" };

// ---------- 格式 ----------
const unitDiv = () => (state.unit === "t" ? 1000 : 1);
const unitLabel = () => (state.unit === "t" ? "tCO₂e" : "kgCO₂e");
const fmt = (kg, signed) => {
  const v = kg / unitDiv();
  const s = Math.abs(v).toLocaleString("zh-TW", { maximumFractionDigits: state.unit === "t" ? 1 : 0 });
  return (v < 0 ? "−" : signed && v > 0 ? "+" : "") + s;
};
const num = (v, d = 2) => Number(v).toLocaleString("zh-TW", { maximumFractionDigits: d });
const pct = (x, d = 1) => (x * 100).toFixed(d) + "%";
const slot = (title, hint, cls = "short") => `<div class="slot ${cls}"><div class="slot-ico">▭</div><b>${title}</b><p class="muted small">${hint}</p></div>`;

function toast(msg, ms = 2600) {
  const t = $("#toast");
  t.textContent = msg; t.hidden = false;
  clearTimeout(toast.t); toast.t = setTimeout(() => (t.hidden = true), ms);
}
function bindTips(root) {
  const tip = $("#tip");
  $$("[data-tip]", root).forEach((el) => {
    el.addEventListener("mousemove", (e) => {
      tip.innerHTML = el.dataset.tip; tip.hidden = false;
      tip.style.left = Math.min(e.clientX + 14, innerWidth - 260) + "px"; tip.style.top = e.clientY + 16 + "px";
    });
    el.addEventListener("mouseleave", () => (tip.hidden = true));
  });
}
function setTabs(sel, attr, onPick) {
  const box = $(sel);
  box.addEventListener("click", (e) => {
    const b = e.target.closest(".tab"); if (!b) return;
    $$(".tab", box).forEach((x) => x.classList.toggle("active", x === b));
    onPick(b.dataset[attr]);
  });
}

// ---------- 載入資料 ----------
const std = () => state.data?.method === "standard_formula";

// 外掛目前輸出的格式（標準公式，無 format 欄位）：有 project、result、by_component
const isLegacy = (o) => o.project && o.result && Array.isArray(o.by_component);

// 外掛結果 JSON 的 structure.bim：Revit「專案設定」勾選「使用估算」，估算視窗按過「計算並儲存」後才有。
// 轉成 BIM 數量法的畫面資料（quantities、factors、model_description），三份手冊文件與材料別、構件類別、樓層分佈都由此產生。
// 沒有明細（未勾估算，或舊版估算紀錄）時回傳 null，維持標準公式的顯示。
function bimFromPlugin(o) {
  const s = o.structure, b = s?.bim;
  if (s?.method !== "bim_quantity" || !b || !Array.isArray(b.quantities) || !b.quantities.length || !Array.isArray(b.factors)) return null;
  return {
    method: "bim_from_plugin",
    model_description: b.model_description || {},
    quantities: b.quantities,
    factors: b.factors,
    result: {
      above: { CFs: b.totals?.above ?? s.design_CFs },
      below: { CFs_prime: b.totals?.below ?? o.underground?.structure },
      baseline: { CFsc: s.baseline_CFsc, method: "基準案依標準法 式 1-2＋表 4-6" },
    },
  };
}
function fromLegacy(o) {
  const p = o.project, st = o.by_component.find((c) => c.key === "structure") || {};
  const issues = [...(o.issues || [])];
  const groups = new Map();
  for (const u of o.unmapped || []) {
    const c = u.category ?? "", t = u.type ?? u.type_name ?? "", g = groups.get(c + "｜" + t) || { c, t, n: 0, reason: u.reason };
    g.n++; groups.set(c + "｜" + t, g);
  }
  groups.forEach((g) => issues.push({ level: "warning", message: `未對應：${g.c} ${g.t} × ${g.n}。${g.reason ?? ""}（非主結構構件，不計入主結構碳排）` }));
  const bim = bimFromPlugin(o);
  if (o.structure?.method === "bim_quantity" && !bim)
    issues.push({ level: "warning", message: "外掛採用 BIM 估算值（LEBR_CFs），但這份 JSON 沒有估算明細（structure.bim）。請在 Revit 估算視窗重新按「計算並儲存」，再匯出結果 JSON。" });
  const legacy = {
    method: "standard_formula",
    model: { name: String(p.source ?? "").replace(/^目前模型：/, ""), exported_at: String(o.calculated_at ?? "").replace("T", " ").slice(0, 16), manual_version: o.database?.manual_version, db_review_status: o.database?.review_status },
    model_description: {},
    building: { AFu: p.AFu, AFb: p.AFb, S: p.floors, Sb: p.basement_floors },
    quantities: [], factors: [],
    result: { above: { CFs: st.design }, below: { CFs_prime: o.underground?.structure }, baseline: { CFsc: st.baseline, method: "標準法 式 1-2＋表 4-6" } },
    issues, unmapped: [],
  };
  return bim ? { ...legacy, ...bim } : legacy;
}

function normalize(o) {
  if (!o || typeof o !== "object" || Array.isArray(o)) return { error: "檔案不是有效的 JSON 物件。" };
  if (o.format === "lebr-codes") return { error: "這是「匯出代號」的設定檔（lebr-codes），不是計算結果。請在 Revit 結果視窗按「匯出結果 JSON…」。" };
  if (o.format === FORMAT) {
    if (!Array.isArray(o.quantities) || !Array.isArray(o.factors)) return { error: "缺少 quantities 或 factors 陣列。" };
    return { data: o };
  }
  if (isLegacy(o)) return { data: fromLegacy(o) };
  return { error: `無法辨識的格式：需要 "${FORMAT}"，或外掛目前的標準公式結果（含 project、result、by_component）。收到 ${o.format ? `"${o.format}"` : "沒有 format 欄位的檔案"}。` };
}
function showLoadError(msg) {
  ["#load-msg", "#load-msg2"].forEach((s) => { const el = $(s); el.textContent = msg || ""; el.hidden = !msg; });
  if (msg) toast(msg, 5000);
}
// 最後一次的結果存在這台電腦的瀏覽器（與首頁共用）；用計算時間判斷哪個比較新
const SAVED_KEY = "lebr:last-result";
const resultTime = (o) => String(o?.calculated_at ?? o?.model?.exported_at ?? "").replace("T", " ").slice(0, 19);
function loadSaved() {
  try { const s = JSON.parse(localStorage.getItem(SAVED_KEY) || "null"); return s && s.obj ? s : null; } catch (e) { return null; }
}
function newerSaved() {
  const s = loadSaved();
  return s && state.raw && resultTime(s.obj) > resultTime(state.raw) ? s : null;
}
// fromLink：連結或舊分頁重新整理帶進來的資料，比這台電腦存的舊就不覆蓋
function loadObject(obj, label, fromLink) {
  const r = normalize(obj);
  showLoadError(r.error);
  if (r.error) return false;
  state.data = r.data; state.raw = obj; state.calc = compute(r.data); state.source = label; state.sel = null;
  renderAll();
  // 記住這次的結果，回首頁時 3D 也顯示同一個模型（首頁示範資料不記）
  if (obj.model_description?.software !== "首頁示範資料" && !(fromLink && newerSaved())) {
    try { localStorage.setItem(SAVED_KEY, JSON.stringify({ label: r.data.model?.name || label, obj })); } catch (e) { /* 私密視窗或容量不足：不影響顯示 */ }
  }
  return true;
}
function showLatest(msg) {
  const s = loadSaved();
  if (!s || !loadObject(s.obj, s.label || "最新結果", true)) return;
  history.replaceState(null, "", location.pathname + location.search);
  if (msg) toast(msg + "（" + resultTime(s.obj).slice(0, 16) + "）", 4000);
}
async function loadFile(file) {
  if (!file) return;
  try { loadObject(JSON.parse(await file.text()), file.name); }
  catch (e) { showLoadError("無法讀取檔案：不是有效的 JSON。"); }
}

// 分享連結：JSON → gzip → base64url，放在 #d= 後面
const b64u = (buf) => btoa(String.fromCharCode(...new Uint8Array(buf))).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
const unb64u = (s) => Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
async function encodeData(obj) {
  const stream = new Blob([JSON.stringify(obj)]).stream().pipeThrough(new CompressionStream("gzip"));
  return b64u(await new Response(stream).arrayBuffer());
}
async function decodeData(s) {
  const stream = new Blob([unb64u(s)]).stream().pipeThrough(new DecompressionStream("gzip"));
  return JSON.parse(await new Response(stream).text());
}
async function copyShareLink() {
  if (!state.data) return toast("尚無資料可分享");
  const link = location.href.split("#")[0] + "#d=" + (await encodeData(state.raw ?? state.data));
  try { await navigator.clipboard.writeText(link); } catch (e) { return toast("瀏覽器不允許複製"); }
  toast(link.length > 8000 ? `已複製（連結長達 ${link.length} 字元，部分瀏覽器或通訊軟體可能截斷，建議改傳 JSON）` : "已複製分享連結", link.length > 8000 ? 6000 : 2600);
}

// ---------- 計算（數量 × 附錄一係數） ----------
function compute(d) {
  const F = new Map((d.factors || []).map((f) => [f.id, f]));
  const rows = [], bad = [];
  for (const q of d.quantities) {
    const f = F.get(q.material_id);
    if (!f) { bad.push({ q, reason: "附錄一係數表沒有這個材料編號" }); continue; }
    if (f.unit !== q.unit) { bad.push({ q, reason: `單位不符：數量為 ${q.unit}，係數為 ${f.unit}` }); continue; }
    if (!(q.quantity > 0)) { bad.push({ q, reason: "數量為 0 或負值" }); continue; }
    const st = [f.a1, f.a2, f.a3, f.a4].map((v) => (v || 0) * q.quantity);
    const carbon = f.total * q.quantity;
    st.push(Math.max(0, carbon - st.reduce((a, b) => a + b, 0))); // 手冊合併欄位的剩餘量
    rows.push({ ...q, f, stages: st, carbon });
  }
  const sum = (zone) => rows.filter((r) => r.zone === zone).reduce((a, r) => a + r.carbon, 0);
  return { rows, bad, above: sum("above"), below: sum("below") };
}
function groupBy(rows, keyFn) {
  const m = new Map();
  for (const r of rows) {
    const k = keyFn(r);
    const g = m.get(k) || { key: k, carbon: 0, stages: [0, 0, 0, 0, 0], rows: [], qty: {} };
    g.carbon += r.carbon; r.stages.forEach((v, i) => (g.stages[i] += v)); g.rows.push(r);
    g.qty[r.unit] = (g.qty[r.unit] || 0) + r.quantity;
    m.set(k, g);
  }
  return [...m.values()];
}
const zoneRows = (z = state.zone) => state.calc.rows.filter((r) => r.zone === z);
const qtyText = (qty) => Object.entries(qty).map(([u, v]) => `${num(v)} ${u}`).join(" ／ ");

function checkResult(zone) {
  if (std()) return { status: "none", calc: 0 };
  const reported = zone === "above" ? state.data.result?.above?.CFs : state.data.result?.below?.CFs_prime;
  const calc = state.calc[zone];
  if (reported == null) return { status: "none", calc };
  const diff = reported ? (calc - reported) / reported : 0;
  return { status: Math.abs(diff) <= CHECK_TOL ? "ok" : "diff", calc, reported, diff };
}

// ---------- 版頭 ----------
function renderHeader() {
  const d = state.data, newer = d && newerSaved();
  const draft = d?.model?.db_review_status === "draft";
  $("#draft-banner").hidden = !(draft && $("#opt-banner").checked);
  $("#proj-sub").textContent = d ? `${d.model?.name ?? "（未命名模型）"} · ${d.model?.exported_at ?? ""} · ${std() ? "標準公式（式 1～7）" : "BIM 模型估算"}` : "BIM 模型估算 · 依手冊附錄一計算";
  $("#source-card").innerHTML = d
    ? `<small class="muted">資料來源</small><b><i class="dot ${newer ? "a" : "g"}"></i>${esc(state.source)}</b><small class="muted">計算時間 ${esc(resultTime(state.raw).slice(0, 16) || "—")}</small><small class="muted">手冊 ${esc(d.model?.manual_version ?? "?")} 版 · ${draft ? "資料庫草稿" : "資料庫已核對"}</small>`
      + (newer ? `<small style="color:var(--amber)">這不是最新的結果，這台電腦有 ${esc(resultTime(newer.obj).slice(0, 16))} 算的</small><button class="btn mini" id="btn-latest">改看最新結果</button>` : "")
    : `<small class="muted">資料來源</small><b><i class="dot"></i>尚未載入</b><small class="muted">等待 Revit 傳入結果</small>`;
}

// ---------- 總覽 ----------
/** LEBR 等級尺（表 6-1）：各級 ECI 上限，標出目前等級。只計主結構時外掛不會給等級，就不顯示。 */
const GRADE_COLOR = { "1+": "#34c759", "1": "#5ac8fa", "2": "#3b82f6", "3": "#8b7cf6", "4": "#e0a43a", "5": "#ff9f0a", "6": "#e2674f", "7": "#ff453a" };
function renderGrade() {
  const res = state.raw?.result, scale = state.raw?.grade_scale;
  if (!res?.grade || !Array.isArray(scale) || !scale.length) { $("#grade").innerHTML = ""; return; }
  const cur = String(res.grade);
  const cell = (g, i) => {
    const prev = scale[i - 1];
    const limit = g.eci != null ? "≦ " + num(g.eci, 1) : prev ? `≧ ${esc(prev.grade)} 級上限` : "—";
    return `<div class="grade-cell${String(g.grade) === cur ? " cur" : ""}" style="--g:${GRADE_COLOR[g.grade] || "#8e8e93"}">
      ${String(g.grade) === cur ? `<span class="grade-flag">目前 ${esc(cur)} 級</span>` : ""}<b>${esc(g.grade)}</b><small>${limit}</small></div>`;
  };
  $("#grade").innerHTML = `<div class="chart-card grade-card">
    <div class="grade-head"><h2>等級尺</h2><span class="muted small">LEBR ${esc(cur)} 級 · 減碳率 CFR ${res.CFR != null ? (res.CFR * 100).toFixed(2) + "%" : "—"} · ECI ${res.ECI != null ? num(res.ECI, 2) : "—"} kgCO₂e/m² · 表 6-1</span></div>
    <div class="grade-scale">${scale.map(cell).join("")}</div></div>`;
}

function renderOverview() {
  $("#empty-hero").hidden = !!state.data; $("#ov").hidden = !state.data;
  if (!state.data) return;
  renderGrade();
  const d = state.data, c = state.calc, r = d.result || {}, b = d.building || {};
  const cfs = r.above?.CFs ?? c.above, cfsc = r.baseline?.CFsc;
  const afu = b.AFu, base = cfsc != null ? cfsc : null;
  $("#kpis").innerHTML = `
    <div class="kpi hero-kpi" style="--gc:#1a73ff"><span>地上層主結構碳排 CFs（${unitLabel()}）</span><strong>${fmt(cfs)}</strong><small>${std() ? "標準公式（式 1～7）" : "圖台驗算 " + fmt(c.above)}</small></div>
    <div class="kpi"><span>比較參考：基準案 CFsc（${unitLabel()}）</span><strong>${base != null ? fmt(base) : "—"}</strong><small>${base != null ? `比基準案${base >= cfs ? "少" : "多"}排 ${fmt(Math.abs(base - cfs))}（${pct(Math.abs(base - cfs) / base)}）· ${esc(r.baseline?.method ?? "")}` : "選填；未提供不影響主結構結果"}</small></div>
    <div class="kpi"><span>單位面積碳排（kgCO₂e/m²）</span><strong>${afu ? num(cfs / afu, 1) : "—"}</strong><small>${afu ? (base != null ? `基準案 ${num(base / afu, 1)} · ` : "") + `AFu ${num(afu, 1)} m²` : "缺少 AFu"}</small></div>
    <div class="kpi"><span>地下層 CFs′（${unitLabel()}）</span><strong>${fmt(r.below?.CFs_prime ?? c.below)}</strong><small>不計入減碳評估（手冊 p.25）</small></div>`;

  if (std()) {
    $("#check").innerHTML = `<div class="check-strip"><b>計算方法</b><span class="muted small">標準公式（手冊式 1～7）：輸入是整棟的幾何與設計參數，結果沒有材料與數量明細，所以無法做「數量 × 附錄一係數」的驗算。</span></div>`;
    $("#stage-legend").innerHTML = "";
    $("#mat-chart").innerHTML = slot("材料別碳排視窗", state.raw?.structure?.method === "bim_quantity"
      ? "外掛已採 BIM 估算，但這份 JSON 沒有明細：請在 Revit 估算視窗重新按「計算並儲存」後再匯出。"
      : "需要 BIM 數量法的資料（混凝土、鋼筋、型鋼的數量），外掛尚未提供。", "short");
    $("#cat-chart").innerHTML = slot("構件類別視窗", "需要每個結構元件的類別與數量。", "short");
    $("#lvl-chart").innerHTML = slot("樓層分佈視窗", "需要每個結構元件的樓層與數量。", "short");
    return;
  }
  const ck = [checkResult("above"), checkResult("below")];
  const line = (z, k) => k.status === "none" ? `<span class="badge amber">${ZONE[z]}：外掛未提供總數，無法比對</span>`
    : k.status === "ok" ? `<span class="badge">${ZONE[z]}：驗算一致（差 ${pct(Math.abs(k.diff), 2)}）</span>`
    : `<span class="badge red">${ZONE[z]}：驗算不一致，圖台 ${fmt(k.calc)} vs 外掛 ${fmt(k.reported)}（${pct(k.diff, 2)}）</span>`;
  $("#check").innerHTML = `<div class="check-strip"><b>驗算</b><span class="muted small">數量 × 附錄一係數加總，與外掛回傳的總數比對</span>${line("above", ck[0])}${line("below", ck[1])}</div>`;

  $("#stage-legend").innerHTML = STAGES.map(([n, col]) => `<i class="lg" style="background:${col}"></i>${n}`).join("");
  const rows = zoneRows();
  if (!rows.length) { ["#mat-chart", "#cat-chart", "#lvl-chart"].forEach((s) => ($(s).innerHTML = `<p class="empty">${ZONE[state.zone]}沒有結構元件數量。</p>`)); return; }
  const total = rows.reduce((a, x) => a + x.carbon, 0);

  const mats = groupBy(rows, (x) => x.material_id).sort((a, z) => z.carbon - a.carbon);
  const maxM = mats[0].carbon;
  $("#mat-chart").innerHTML = mats.map((g) => {
    const f = g.rows[0].f;
    const tip = `<b>${esc(f.name)}</b><br>${esc(g.key)} · ${qtyText(g.qty)}<br>碳排 ${fmt(g.carbon)}（${pct(g.carbon / total)}）`;
    const seg = g.stages.map((v, i) => `<i style="width:${v / maxM * 100}%;background:${STAGES[i][1]}"></i>`).join("");
    const sel = state.sel?.type === "material" && state.sel.key === g.key;
    return `<div class="hb clickable ${sel ? "sel" : ""}" data-sel="material:${esc(g.key)}" data-tip="${esc(tip)}"><div class="hb-name">${esc(f.name)}</div><div class="stack">${seg}</div><div class="hb-val">${fmt(g.carbon)}<small>${pct(g.carbon / total)}</small></div></div>`;
  }).join("");

  const bars = (groups, kind, label) => {
    const mx = Math.max(...groups.map((g) => g.carbon));
    return groups.map((g) => {
      const sel = state.sel?.type === kind && state.sel.key === g.key;
      const tip = `<b>${esc(label(g.key))}</b><br>${qtyText(g.qty)}<br>碳排 ${fmt(g.carbon)}（${pct(g.carbon / total)}）`;
      return `<div class="hb clickable ${sel ? "sel" : ""}" data-sel="${kind}:${esc(g.key)}" data-tip="${esc(tip)}"><div class="hb-name">${esc(label(g.key))}</div><div class="bar-pair"><i class="bar" style="width:${g.carbon / mx * 100}%"></i></div><div class="hb-val">${fmt(g.carbon)}<small>${pct(g.carbon / total)}</small></div></div>`;
    }).join("");
  };
  $("#cat-chart").innerHTML = bars(groupBy(rows, (x) => x.category).sort((a, z) => z.carbon - a.carbon), "category", (k) => CAT[k] || k);
  $("#lvl-chart").innerHTML = bars(groupBy(rows, (x) => x.level), "level", (k) => k);
  $$("#ov [data-sel]").forEach((el) => (el.onclick = () => { const [t, ...k] = el.dataset.sel.split(":"); select({ type: t, key: k.join(":") }); }));
  bindTips($("#ov"));
}

// ---------- 模型說明 ----------
function renderDesc() {
  const d = state.data;
  const md = d?.model_description || {};
  const has = (v) => (Array.isArray(v) ? v.length : !!v);
  const items = [
    ["① 估算模型說明", d && ["software", "scope", "assumptions"].some((k) => has(md[k]))],
    ["② 鋼筋、混凝土數量估算報告書", d && d.quantities.length > 0],
    ["③ 依附錄一計算的地上層與地下層碳排報告", d && state.calc.rows.length > 0],
  ];
  $("#checklist").innerHTML = items.map(([t, ok]) => `<div class="check-item ${ok ? "ok" : ""}"><span>${ok ? "✔" : "○"}</span>${t}<em>${d ? (ok ? "已備" : "缺資料") : "尚未載入"}</em></div>`).join("");
  if (!d) { $("#desc-body").innerHTML = slot("模型說明視窗", "載入結果後，這裡顯示模型軟體、建模範圍、假設與鋼筋估算方式。"); return; }
  if (std()) { $("#desc-body").innerHTML = slot("此結果來自標準公式", "三份手冊文件屬於 BIM 模型估算（數量法），外掛尚未輸出對應資料。"); return; }
  const rebar = md.rebar ? (md.rebar.mode === "ratio" ? `配筋率估算：${md.rebar.kg_per_m3 ?? "?"} kg/m³ 混凝土` : md.rebar.mode === "modeled" ? "模型中已建置鋼筋" : md.rebar.mode) : null;
  const field = (t, v) => `<div class="info-grid-item"><small>${t}</small><b>${v ? esc(v) : '<span class="neg">未提供</span>'}</b></div>`;
  $("#desc-body").innerHTML = `
    <div class="info-grid wide">${field("建模軟體與版本", md.software)}${field("建模範圍與深度", md.scope)}${field("鋼筋估算方式", rebar)}</div>
    <div class="chart-card" style="margin-top:14px"><h3 class="card-h">假設與限制</h3>${has(md.assumptions) ? `<ul class="plain">${md.assumptions.map((a) => `<li>${esc(a)}</li>`).join("")}</ul>` : `<p class="empty">外掛未提供假設清單。</p>`}</div>`;
}

// ---------- 數量報告 ----------
function qtyRows() {
  const q = state.q.trim().toLowerCase();
  return state.calc.rows.filter((r) => (state.fz === "all" || r.zone === state.fz) && (state.fc === "all" || r.category === state.fc)
    && (!q || (r.f.name + r.material_id + r.level).toLowerCase().includes(q)));
}
function renderQty() {
  const d = state.data;
  const cats = d ? [...new Set(d.quantities.map((x) => x.category))] : [];
  $("#f-cat").innerHTML = `<option value="all">全部類別</option>` + cats.map((c) => `<option value="${c}">${esc(CAT[c] || c)}</option>`).join("");
  $("#f-cat").value = state.fc;
  if (!d || std()) {
    const why = std() ? "此結果來自標準公式，沒有材料數量；需要 BIM 數量法的資料。" : "";
    $("#qty-sum").innerHTML = slot("數量彙總視窗", why || "按材料合計混凝土、鋼筋、型鋼的數量。");
    $("#qty-table").innerHTML = slot("數量明細視窗", why || "按構件類別、樓層、材料列出。");
    return;
  }
  const rows = qtyRows();
  if (!rows.length) { $("#qty-sum").innerHTML = `<p class="empty">沒有符合條件的數量。</p>`; $("#qty-table").innerHTML = ""; return; }
  const bm = groupBy(rows, (r) => r.material_id);
  $("#qty-sum").innerHTML = `<table class="table"><thead><tr><th>材料</th><th>附錄一編號</th><th class="num">合計數量</th><th class="num">元件數</th></tr></thead><tbody>${
    bm.map((g) => `<tr><td>${esc(g.rows[0].f.name)}</td><td><span class="code">${esc(g.key)}</span></td><td class="num">${qtyText(g.qty)}</td><td class="num">${g.rows.length}</td></tr>`).join("")}</tbody></table>`;
  const det = groupBy(rows, (r) => [r.zone, r.category, r.level, r.material_id].join("|"));
  $("#qty-table").innerHTML = `<table class="table"><thead><tr><th>範圍</th><th>類別</th><th>樓層</th><th>材料</th><th class="num">數量</th><th class="num">元件數</th><th></th></tr></thead><tbody>${
    det.map((g, i) => { const r0 = g.rows[0]; return `<tr><td>${ZONE[r0.zone]}</td><td>${esc(CAT[r0.category] || r0.category)}</td><td>${esc(r0.level)}</td><td>${esc(r0.f.name)}</td><td class="num">${qtyText(g.qty)}</td><td class="num">${g.rows.length}</td><td><button class="btn mini" data-ids="${i}">複製 ID</button></td></tr>`; }).join("")}</tbody></table>`;
  $$("#qty-table [data-ids]").forEach((b) => (b.onclick = () => copyIds(det[+b.dataset.ids].rows)));
}
function copyIds(rows) {
  const ids = [...new Set(rows.map((r) => r.element_id).filter((x) => x != null))];
  if (!ids.length) return toast("這些數量沒有對應的 ElementId");
  navigator.clipboard?.writeText(ids.join(", ")).then(() => toast(`已複製 ${ids.length} 個 ElementId，可貼到 Revit 選取`), () => toast("瀏覽器不允許複製"));
}

// ---------- 碳排報告 ----------
function renderCarbon() {
  const d = state.data;
  if (!d || std()) { $("#carbon-body").innerHTML = slot("碳排計算報告視窗", std() ? "此結果來自標準公式，沒有逐材料的計算；需要 BIM 數量法的資料。" : "地上層與地下層分開列出：數量 × 附錄一係數 = 碳排，並附編號與頁碼。", "short"); return; }
  $("#carbon-body").innerHTML = ["above", "below"].map((z) => {
    const rows = zoneRows(z), ck = checkResult(z);
    const head = `<div class="section-head"><h2>${ZONE[z]}${z === "above" ? "主結構 CFs" : "主結構 CFs′"}</h2>${z === "below" ? '<span class="muted small">不計入減碳評估</span>' : ""}</div>`;
    if (!rows.length) return head + `<div class="table-wrap"><p class="empty">${ZONE[z]}沒有結構元件數量。</p></div>`;
    const g = groupBy(rows, (r) => r.material_id).sort((a, b) => b.carbon - a.carbon);
    const foot = ck.status === "none" ? "外掛未提供總數" : ck.status === "ok" ? `與外掛總數一致（差 ${pct(Math.abs(ck.diff), 2)}）` : `<span class="neg">與外掛總數不一致：外掛 ${fmt(ck.reported)}，差 ${pct(ck.diff, 2)}</span>`;
    return head + `<div class="table-wrap"><table class="table"><thead><tr><th>附錄一編號</th><th>材料</th><th>單位</th><th class="num">數量</th><th class="num">A1</th><th class="num">A2</th><th class="num">A3</th><th class="num">A4</th><th class="num">係數合計</th><th class="num">碳排（${unitLabel()}）</th><th class="num">頁碼</th></tr></thead><tbody>${
      g.map((x) => { const f = x.rows[0].f, u = x.rows[0].unit; return `<tr><td><span class="code">${esc(x.key)}</span></td><td>${esc(f.name)}</td><td>${esc(u)}</td><td class="num">${num(x.qty[u])}</td><td class="num">${num(f.a1 ?? 0, 3)}</td><td class="num">${num(f.a2 ?? 0, 3)}</td><td class="num">${num(f.a3 ?? 0, 3)}</td><td class="num">${num(f.a4 ?? 0, 3)}</td><td class="num">${num(f.total, 3)}</td><td class="num">${fmt(x.carbon)}</td><td class="num">${f.source_page ?? "—"}</td></tr>`; }).join("")}
      <tr class="total"><td colspan="9">合計　<span class="muted small">${foot}</span></td><td class="num"><b>${fmt(state.calc[z])}</b></td><td></td></tr></tbody></table></div>`;
  }).join("");
}

// ---------- 提醒與查核 ----------
function collectIssues() {
  const d = state.data, out = [];
  (d.issues || []).forEach((i) => out.push({ level: i.level, message: i.message, from: "外掛" }));
  state.calc.bad.forEach((b) => out.push({ level: "error", message: `${b.reason}：${b.q.material_id ?? "（無編號）"}，樓層 ${b.q.level ?? "?"}，元件 ${b.q.element_id ?? "?"}`, from: "圖台查核" }));
  ["above", "below"].forEach((z) => { const k = checkResult(z); if (k.status === "diff") out.push({ level: "warning", message: `${ZONE[z]}碳排驗算不一致：圖台 ${fmt(k.calc)}，外掛 ${fmt(k.reported)}（${pct(k.diff, 2)}）`, from: "圖台查核" }); });
  (d.unmapped || []).forEach((u) => out.push({ level: "warning", message: `未對應元件：${u.category ?? ""} ${u.type_name ?? u.type ?? ""}（${u.element_id ?? u.id ?? "?"}）${u.reason ? "，" + u.reason : ""}`, from: "外掛" }));
  if (!(d.building?.AFu > 0)) out.push({ level: "warning", message: "缺少地上樓地板面積 AFu，無法算單位面積碳排。", from: "圖台查核" });
  return out;
}
function renderNotices() {
  const n = $("#notice-count");
  if (!state.data) { $("#issue-list").innerHTML = `<p class="empty">尚無資料。</p>`; n.hidden = true; return; }
  const all = collectIssues();
  n.textContent = all.length; n.hidden = !all.length;
  const list = all.filter((x) => state.level === "all" || x.level === state.level);
  $("#issue-list").innerHTML = list.length
    ? list.map((x) => { const [t, c] = LEVEL[x.level] || ["資訊", "blue"]; return `<div class="issue"><span class="badge ${c}">${t}</span><p>${esc(x.message)}<small class="muted"> · ${x.from}</small></p></div>`; }).join("")
    : `<p class="empty">✅ 沒有此等級的提醒。</p>`;
}

// ---------- 右側詳情 ----------
function select(sel) { state.sel = sel; renderOverview(); renderPanel(); }
function renderPanel() {
  const d = state.data, s = state.sel;
  let html;
  if (!d) {
    html = `<div class="panel-head"><h2>詳情</h2></div><div class="hint">載入結果後，點選圖表中的材料、類別或樓層，詳細資料會顯示在這裡。</div>${slot("詳情視窗", "待載入", "short")}`;
  } else if (!s) {
    const b = d.building || {}, m = d.model || {};
    html = `<div class="panel-head"><h2>專案資訊</h2></div><div class="info-grid">
      ${[["地上樓地板面積", b.AFu != null ? num(b.AFu, 1) + " m²" : "—"], ["地下樓地板面積", b.AFb != null ? num(b.AFb, 1) + " m²" : "—"], ["地上層數", b.S ?? "—"], ["地下層數", b.Sb ?? "—"], ["手冊版本", m.manual_version ?? "—"], ["資料庫", m.db_review_status ?? "—"]]
        .map(([k, v]) => `<div><small>${k}</small><b>${esc(v)}</b></div>`).join("")}</div>
      <div class="hint">點選左側圖表中的項目，這裡會顯示詳情。</div>`;
  } else {
    const rows = zoneRows().filter((r) => (s.type === "material" ? r.material_id : s.type === "category" ? r.category : r.level) === s.key);
    const total = zoneRows().reduce((a, r) => a + r.carbon, 0), c = rows.reduce((a, r) => a + r.carbon, 0);
    const title = s.type === "material" ? (rows[0]?.f.name ?? s.key) : s.type === "category" ? CAT[s.key] || s.key : s.key;
    const by = groupBy(rows, (r) => (s.type === "material" ? r.category + "|" + r.level : r.material_id)).sort((a, z) => z.carbon - a.carbon).slice(0, 6);
    const f = s.type === "material" ? rows[0]?.f : null;
    html = `<div class="panel-head"><h2>${esc(title)}</h2><button class="icon-btn" id="p-close" title="取消選取">✕</button></div>
      <div class="meta">${ZONE[state.zone]} · ${s.type === "material" ? "材料" : s.type === "category" ? "構件類別" : "樓層"}</div>
      <div class="info-grid"><div><small>碳排</small><b>${fmt(c)}</b></div><div><small>占${ZONE[state.zone]}</small><b>${total ? pct(c / total) : "—"}</b></div>
      <div><small>數量</small><b>${qtyText(groupBy(rows, () => 0)[0]?.qty ?? {}) || "—"}</b></div><div><small>元件數</small><b>${rows.length}</b></div></div>
      ${f ? `<div class="goals"><h3>附錄一係數（${esc(f.unit)}）</h3><div class="coef">${["a1", "a2", "a3", "a4"].map((k, i) => `<span><small>A${i + 1}</small>${num(f[k] ?? 0, 3)}</span>`).join("")}<span><small>合計</small><b>${num(f.total, 3)}</b></span></div><p class="muted small">${esc(f.id)} · 手冊第 ${f.source_page ?? "?"} 頁</p></div>` : ""}
      <div class="goals"><h3>主要組成</h3>${by.map((g) => `<div class="mini-row"><span class="small">${esc(s.type === "material" ? g.key.replace("|", " · ").replace(/^(\w+)/, (m) => CAT[m] || m) : g.rows[0].f.name)}</span><span class="muted small">${fmt(g.carbon)}</span></div>`).join("")}</div>
      <button class="btn block" id="p-copy">複製 ElementId</button>`;
  }
  $("#panel").innerHTML = html;
  const cl = $("#p-close"); if (cl) cl.onclick = () => select(null);
  const cp = $("#p-copy"); if (cp) cp.onclick = () => copyIds(zoneRows().filter((r) => (s.type === "material" ? r.material_id : s.type === "category" ? r.category : r.level) === s.key));
}

// ---------- 匯出 ----------
function downloadCsv(name, rows) {
  const q = (v) => `"${String(v ?? "").replace(/"/g, '""')}"`;
  const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(new Blob(["﻿" + rows.map((r) => r.map(q).join(",")).join("\r\n")], { type: "text/csv;charset=utf-8" })), download: name });
  a.click(); URL.revokeObjectURL(a.href);
}
const draftNote = () => (state.data.model?.db_review_status === "draft" ? "資料庫為草稿，僅供參考" : "");
function exportQty() {
  if (!state.data) return toast("尚無資料可匯出");
  downloadCsv("lebr_structure_quantities.csv", [["範圍", "類別", "樓層", "材料編號", "材料", "單位", "數量", "ElementId", "備註"],
    ...qtyRows().map((r) => [ZONE[r.zone], CAT[r.category] || r.category, r.level, r.material_id, r.f.name, r.unit, r.quantity, r.element_id ?? "", draftNote()])]);
}
function exportCarbon() {
  if (!state.data) return toast("尚無資料可匯出");
  downloadCsv("lebr_structure_carbon.csv", [["範圍", "附錄一編號", "材料", "單位", "數量", "A1", "A2", "A3", "A4", "係數合計", "碳排kgCO2e", "頁碼", "備註"],
    ...state.calc.rows.map((r) => [ZONE[r.zone], r.material_id, r.f.name, r.unit, r.quantity, r.f.a1 ?? "", r.f.a2 ?? "", r.f.a3 ?? "", r.f.a4 ?? "", r.f.total, r.carbon, r.f.source_page ?? "", draftNote()])]);
}

// ---------- 主流程 ----------
function showView(v) {
  $$("#nav a").forEach((a) => a.classList.toggle("active", a.dataset.view === v));
  $$(".view").forEach((s) => s.classList.toggle("active", s.id === "view-" + v));
}
function renderAll() {
  renderHeader(); renderOverview(); renderDesc(); renderQty(); renderCarbon(); renderNotices(); renderPanel();
}

async function init() {
  renderAll();
  $("#nav").addEventListener("click", (e) => { const a = e.target.closest("a"); if (a) showView(a.dataset.view); });
  setTabs("#unit-tabs", "unit", (u) => { state.unit = u; renderAll(); });
  setTabs("#zone-tabs", "zone", (z) => { state.zone = z; state.sel = null; renderOverview(); renderPanel(); });
  setTabs("#level-tabs", "level", (l) => { state.level = l; renderNotices(); });
  $("#q").oninput = (e) => { state.q = e.target.value; renderQty(); };
  $("#f-zone").onchange = (e) => { state.fz = e.target.value; renderQty(); };
  $("#f-cat").onchange = (e) => { state.fc = e.target.value; renderQty(); };
  $("#opt-banner").onchange = renderHeader;
  $("#ex-qty").onclick = exportQty;
  $("#ex-carbon").onclick = exportCarbon;
  $("#btn-share").onclick = $("#btn-share2").onclick = copyShareLink;
  $("#source-card").addEventListener("click", (e) => { if (e.target.id === "btn-latest") showLatest("已換成最新的計算結果"); });
  // 另一個分頁（首頁或 Dashboard）載入了較新的結果：這頁跟著換，避免看到舊的
  addEventListener("storage", (e) => { if (e.key === SAVED_KEY && (!state.data || newerSaved())) showLatest("其他分頁有較新的計算結果，已自動換成"); });
  $("#btn-clear").onclick = () => { state.data = state.calc = null; state.sel = null; state.source = ""; history.replaceState(null, "", location.pathname + location.search); showLoadError(""); renderAll(); };
  [$("#file"), $("#file2")].forEach((el) => (el.onchange = (e) => { loadFile(e.target.files[0]); e.target.value = ""; }));

  // 拖放
  const veil = $("#dropveil");
  let depth = 0;
  addEventListener("dragenter", (e) => { e.preventDefault(); depth++; veil.hidden = false; });
  addEventListener("dragleave", () => { if (--depth <= 0) { depth = 0; veil.hidden = true; } });
  addEventListener("dragover", (e) => e.preventDefault());
  addEventListener("drop", (e) => { e.preventDefault(); depth = 0; veil.hidden = true; loadFile(e.dataTransfer.files[0]); });

  // 從 Revit「在圖台開啟」：#d=<gzip+base64url>
  // 沒有連結時顯示這台電腦上次的結果
  const m = location.hash.match(/^#d=([\w-]+)$/);
  if (m) {
    try {
      loadObject(await decodeData(m[1]), "Revit 連結", true);
      if (newerSaved()) toast("這個連結不是最新的結果，可在左下「資料來源」改看最新結果", 6000);
    }
    catch (e) { showLoadError("連結內的資料無法解讀，請改用「載入 JSON」。"); }
  } else {
    const s = loadSaved();
    if (s) loadObject(s.obj, s.label || "上次的結果", true);
  }
}
init();
