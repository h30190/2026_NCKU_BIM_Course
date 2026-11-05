// LEBR 碳排圖台：計算詳情與註記（疊在 app.js 之上，沿用 app.js 的 state、$、esc、fmt 等）
// 計算詳情：BIM 數量法依 quantities × factors 逐層列出 CFsn = An × L + Bn × M；
//           外掛結果 JSON 有 calculation_steps 時，直接列出外掛的手冊公式與代入過程。
// 註記：存在這台電腦的瀏覽器（localStorage），會一起放進分享連結；也可以匯出／匯入 JSON，
//       外掛「匯出結果 JSON…」帶出的 notes 會自動併入。

const BASIS = { precise: ["精算", ""], estimated: ["估算", "amber"], mixed: ["精算＋估算", "blue"] };
const basisBadge = (b, title) => { const x = BASIS[b] || BASIS.estimated; return `<span class="badge ${x[1]}"${title ? ` title="${esc(title)}"` : ""}>${x[0]}</span>`; };
// 外掛結果 JSON 一律被 app.js 當成標準公式；有「BIM 模型估算」步驟時其實是勾選了「使用估算」
const fromEstimate = () => !!state.raw?.calculation_steps?.some((s) => s.key === "structure_estimate") || state.raw?.structure?.method === "bim_quantity";
const isStdMethod = () => std() && !fromEstimate();
const methodBasis = () => (isStdMethod() ? "estimated" : "precise");
const noteBtn = (target, label) => `<button class="btn mini note-add" data-target="${esc(target)}" data-label="${esc(label)}">＋ 加註記</button>`;

// ================================================================ 計算詳情

function renderDetail() {
  const box = $("#detail-body");
  const d = state.data;
  if (!d) { box.innerHTML = slot("計算詳情", "載入結果後，這裡會依手冊公式列出每一步的代入過程。", "short"); return; }
  const steps = state.raw?.calculation_steps;
  box.innerHTML = methodCard() + (Array.isArray(steps) && steps.length ? pluginSteps(steps) : std() ? stdSteps() : bimSteps());
  bindNoteButtons(box);
  $$(".note-count", box).forEach((el) => { const n = notesFor(el.dataset.target).length; el.textContent = n ? `${n} 則註記` : ""; });
}

function methodCard() {
  const isStd = isStdMethod();
  return `<div class="chart-card method-card">
    <div class="chart-head"><h2>計算方法</h2>${basisBadge(methodBasis())}</div>
    <p>${isStd
      ? "主結構依手冊<b>標準計算法（式 1～7）</b>：由樓層數、跨距、形狀係數等結構參數推估每 m² 碳排，屬<b>估算</b>。Revit 專案設定沒有勾選「使用估算」，或勾選後尚未計算並儲存。"
      : "主結構依手冊 p.24 <b>BIM 模型估算</b>：模型的混凝土、鋼筋數量 × 附錄一 P-LCC 係數，依模型實際數量計算，屬<b>精算</b>。對應 Revit 專案設定勾選「使用估算」。"
        + (fromEstimate() ? "施工、拆除與基準案仍依手冊標準公式（估算），見下方各步驟。" : "")}</p>
    <p class="muted small">精算：依模型實際數量或使用者提供的數值。估算：依手冊標準公式、推估值或預設值。按 ＋ 展開公式、代入數字與係數來源。</p>
  </div>`;
}

/** BIM 數量法：地上、地下各一節，逐層展開。 */
function bimSteps() {
  const c = state.calc, b = state.data.building || {};
  let h = "";
  ["above", "below"].forEach((z, zi) => {
    const rows = c.rows.filter((r) => r.zone === z);
    const sym = z === "above" ? "CFs" : "CFs′";
    const levels = groupBy(rows, (r) => r.level);
    h += `<div class="section-head"><h2>${zi === 0 ? "一" : "二"}、${ZONE[z]}主結構 ${sym}</h2>${z === "below" ? '<span class="muted small">只揭露，不計入減碳評估（手冊 p.25）</span>' : ""}</div>`;
    h += step({
      target: "step:" + z, title: `${ZONE[z]}主結構碳排 ${sym}`, value: c[z], basis: "precise", ref: "附錄一、手冊 p.24",
      formula: `${sym} = Σ CFsn（${ZONE[z]}各樓層）　CFsn = An × L + Bn × M`,
      sub: levels.length ? `${sym} = ${levels.map((l) => fmt(l.carbon)).join(" + ")} = <b>${fmt(c[z])} ${unitLabel()}</b>` : `${ZONE[z]}沒有結構元件數量`,
      inner: levels.map((l) => levelStep(l)).join(""),
    });
  });
  const af = b.AFu;
  h += `<div class="section-head"><h2>三、單位面積碳排與係數</h2></div>`;
  h += step({
    target: "step:intensity", title: "地上層單位面積碳排", value: af ? c.above / af : null, unit: "kgCO₂e/m²", basis: "precise", ref: "",
    formula: "CFs ÷ AFu", sub: af ? `${num(c.above, 0)} ÷ ${num(af, 2)} = <b>${num(c.above / af, 1)} kgCO₂e/m²</b>` : "缺少 AFu",
    params: [["AFu", "地上樓地板面積", af != null ? num(af, 2) + " m²" : "—", "結果 JSON 的 building", "precise"]],
  });
  const used = [...new Set(c.rows.map((r) => r.material_id))].map((id) => c.rows.find((r) => r.material_id === id).f);
  h += step({
    target: "step:factors", title: "附錄一係數（L、M）", valueText: used.length + " 種材料", basis: "precise", ref: "附錄一 P-LCC",
    formula: "係數 = A1 原料開採 + A2 原料運輸 + A3 產品生產 + A4 成品運輸（取「總碳排量」）",
    sub: used.map((f) => `${esc(f.id)} ${esc(f.name)}：${[f.a1, f.a2, f.a3, f.a4].map((v) => num(v ?? 0, 3)).join(" + ")} → <b>${num(f.total, 3)}</b> kgCO₂e/${esc(f.unit)}`).join("<br>"),
    params: used.map((f) => [f.id, f.name, num(f.total, 3) + " /" + f.unit, "手冊第 " + (f.source_page ?? "?") + " 頁", "precise"]),
  });
  return h;
}

function levelStep(l) {
  const conc = l.rows.filter((r) => r.unit !== "kg"), reb = l.rows.filter((r) => r.unit === "kg");
  const an = conc.reduce((a, r) => a + r.quantity, 0), bn = reb.reduce((a, r) => a + r.quantity, 0);
  const cc = conc.reduce((a, r) => a + r.carbon, 0), cr = reb.reduce((a, r) => a + r.carbon, 0);
  const term = (rs) => groupBy(rs, (r) => r.material_id).map((g) => `${num(g.qty[g.rows[0].unit], 2)} ${g.rows[0].unit} × ${num(g.rows[0].f.total, 2)}`).join(" + ") || "0";
  const byCat = groupBy(l.rows, (r) => r.category).sort((a, b) => b.carbon - a.carbon);
  return step({
    target: "level:" + l.key, title: `${l.key}　CFsn`, value: l.carbon, basis: "precise", small: true,
    formula: "CFsn = An × L + Bn × M",
    sub: `= ${term(conc)} + ${term(reb)}<br>= ${fmt(cc)} + ${fmt(cr)} = <b>${fmt(l.carbon)} ${unitLabel()}</b>`,
    params: [
      ["An", "混凝土數量", num(an, 2) + " m³", "模型結構柱、梁、樓板、結構牆體積", "precise"],
      ["Bn", "鋼筋數量", num(bn, 1) + " kg", state.data.model_description?.rebar?.mode === "ratio" ? "配筋率估算" : "模型鋼筋", state.data.model_description?.rebar?.mode === "ratio" ? "estimated" : "precise"],
      ...byCat.map((g) => [CAT[g.key] || g.key, "類別小計", fmt(g.carbon) + " " + unitLabel(), qtyText(g.qty), "precise"]),
    ],
  });
}

/** 外掛的標準公式結果，但沒有 calculation_steps（舊版外掛）。 */
function stdSteps() {
  const r = state.data.result || {}, b = state.data.building || {};
  return `<div class="section-head"><h2>一、主結構（式 1～7）</h2></div>` + step({
    target: "step:structure_cfs", title: "地上主結構碳排 CFs", value: r.above?.CFs, basis: "estimated", ref: "式 1、3、4",
    formula: "CFs = AFu × C × W × LCCR × RN；C 依式 4 由 S、I、SaD、Fu、Sp、D0、L、BH、Rs、F 計算",
    sub: `CFs = <b>${fmt(r.above?.CFs ?? 0)} ${unitLabel()}</b>　基準案 CFsc = ${fmt(r.baseline?.CFsc ?? 0)}　AFu = ${num(b.AFu ?? 0, 2)} m²`,
    params: [["AFu", "地上樓地板面積", num(b.AFu ?? 0, 2) + " m²", "外掛", "precise"]],
    hint: "這份 JSON 是舊版外掛輸出，沒有式 4 的代入過程。用新版外掛（v2 改版）重新匯出就會列出每一步。",
  });
}

/** 新版外掛：calculation_steps 依手冊章節分組。 */
function pluginSteps(steps) {
  const groups = new Map();
  steps.forEach((s) => { const k = s.section || "其他"; if (!groups.has(k)) groups.set(k, []); groups.get(k).push(s); });
  return [...groups].map(([sec, list]) => `<div class="section-head"><h2>${esc(sec)}</h2></div>` + list.map((s) => step({
    target: "step:" + s.key, title: s.title, value: s.value, unit: s.unit, basis: s.basis, ref: s.manual_ref, note: s.basis_note,
    formula: esc(s.formula || "").replace(/\n/g, "<br>"),
    sub: esc(s.substitution || "").replace(/\n/g, "<br>"),
    params: (s.parameters || []).map((p) => [p.symbol, p.name, p.value, p.source, p.basis]),
  })).join("")).join("");
}

function step(o) {
  const val = o.valueText ?? (o.value == null ? "—" : o.unit && !/kgCO2e$|kgCO₂e$/.test(o.unit) ? num(o.value, 2) + " " + o.unit : fmt(o.value) + " " + unitLabel());
  const params = o.params?.length ? `
      <details class="calc sub"><summary><span class="plus"></span>參數與係數（來源、精算或估算）</summary>
        <div class="table-wrap"><table class="table params"><thead><tr><th>符號</th><th>參數／係數</th><th class="num">數值</th><th>來源</th><th>方式</th></tr></thead><tbody>
        ${o.params.map(([sy, n, v, src, b]) => `<tr><td><span class="code">${esc(sy)}</span></td><td>${esc(n)}</td><td class="num">${esc(v)}</td><td class="muted small">${esc(src)}</td><td>${basisBadge(b)}</td></tr>`).join("")}
        </tbody></table></div></details>` : "";
  return `<details class="calc${o.small ? " small-step" : ""}">
    <summary><span class="plus"></span><span class="t">${esc(o.title)}${o.ref ? `<small>${esc(o.ref)}</small>` : ""}</span><span class="note-count" data-target="${esc(o.target)}"></span>${basisBadge(o.basis, o.note)}<b class="v">${val}</b></summary>
    <div class="calc-body">
      <div class="formula"><small>手冊公式</small>${o.formula}</div>
      ${o.sub ? `<div class="formula sub"><small>代入</small>${o.sub}</div>` : ""}
      ${o.note ? `<p class="muted small">${esc(o.note)}</p>` : ""}
      ${o.hint ? `<div class="hint">${esc(o.hint)}</div>` : ""}
      ${params}
      ${o.inner || ""}
      <div class="row-end">${noteBtn(o.target, o.title)}</div>
    </div></details>`;
}

// ================================================================ 註記

const NOTE_KEY = () => "lebr-notes:" + (state.data?.model?.name || "未命名") + "|" + (state.data?.model?.exported_at || "");
const notesState = { list: [], filter: "open" };
const nowText = () => new Date().toLocaleString("sv-SE").slice(0, 16);

function loadNotes() {
  let local = [];
  try { local = JSON.parse(localStorage.getItem(NOTE_KEY()) || "[]"); } catch (e) { local = []; }
  const fromFile = [...(state.raw?.notes || []), ...(state.raw?.web_notes || [])];
  notesState.list = mergeNotes(local, fromFile);
  saveNotes(false);
}
function mergeNotes(a, b) {
  const m = new Map();
  [...a, ...b].forEach((n) => {
    if (!n || !n.id) return;
    const old = m.get(n.id);
    if (!old || String(n.updated_at || "") > String(old.updated_at || "")) m.set(n.id, n);
  });
  return [...m.values()].sort((x, y) => String(y.created_at).localeCompare(String(x.created_at)));
}
function saveNotes(rerender = true) {
  try { localStorage.setItem(NOTE_KEY(), JSON.stringify(notesState.list)); } catch (e) { /* 私密視窗等無法儲存，仍可匯出 */ }
  if (state.raw) state.raw.web_notes = notesState.list; // 分享連結會帶著註記
  if (rerender) { renderNotes(); renderDetail(); }
  updateNoteCount();
}
const liveNotes = () => notesState.list.filter((n) => !n.deleted);
const notesFor = (target) => liveNotes().filter((n) => n.target === target);
function updateNoteCount() {
  const el = $("#note-count"), n = liveNotes().filter((x) => x.status !== "resolved").length;
  el.hidden = !n; el.textContent = n;
}

function noteTargets() {
  const t = [["result", "整份結果"]];
  const steps = state.raw?.calculation_steps;
  if (Array.isArray(steps) && steps.length) steps.forEach((s) => t.push(["step:" + s.key, "計算：" + s.title]));
  else if (state.calc && !std()) {
    ["above", "below"].forEach((z) => t.push(["step:" + z, "計算：" + ZONE[z] + "主結構"]));
    groupBy(state.calc.rows, (r) => r.level).forEach((l) => t.push(["level:" + l.key, "樓層：" + l.key]));
    t.push(["step:factors", "計算：附錄一係數"]);
  }
  (state.raw?.items || []).forEach((i) => t.push(["item:" + i.code, "構件：" + i.code]));
  return t;
}

function renderNotes() {
  const box = $("#notes-body");
  if (!state.data) { box.innerHTML = slot("註記", "載入結果後，可以對整份結果、某個計算步驟或某個樓層加註記。", "short"); return; }
  const author = visitor.name();
  const list = liveNotes().filter((n) => notesState.filter === "all" || (notesState.filter === "open" ? n.status !== "resolved" : n.status === "resolved"));
  box.innerHTML = `
    <div class="chart-card note-form">
      <div class="form row2">
        <label>對象<select class="pill" id="n-target">${noteTargets().map(([k, l]) => `<option value="${esc(k)}">${esc(l)}</option>`).join("")}</select></label>
        <label>註記人<input id="n-author" placeholder="例如：組員 B" value="${esc(author)}"></label>
      </div>
      <label class="n-text">內容<textarea id="n-text" rows="3" placeholder="例如：Sp 請結構組確認；B1 鋼筋量偏高"></textarea></label>
      <div class="row-end"><button class="btn primary" id="n-add">新增註記</button></div>
    </div>
    <div class="section-head">
      <h2>註記（${liveNotes().length}）</h2>
      <div class="toolbar">
        <div class="tabs" id="n-filter">${[["open", "未解決"], ["resolved", "已解決"], ["all", "全部"]].map(([k, l]) => `<button class="tab ${notesState.filter === k ? "active" : ""}" data-f="${k}">${l}</button>`).join("")}</div>
        <button class="btn" id="n-export">匯出註記</button>
        <label class="btn" for="n-import-file">匯入註記</label><input type="file" id="n-import-file" accept=".json" hidden>
      </div>
    </div>
    <div class="chart-card">${list.length ? list.map((n) => `
      <div class="note ${n.status === "resolved" ? "done" : ""}">
        <span class="badge ${n.status === "resolved" ? "" : "amber"}">${n.status === "resolved" ? "已解決" : "未解決"}</span>
        <div class="note-main">
          <div class="note-meta"><b>${esc(n.target_label || n.target)}</b><span class="muted small">${esc(n.author || "（未署名）")} · ${esc(String(n.updated_at || n.created_at || "").replace("T", " ").slice(0, 16))}</span></div>
          <p>${esc(n.text).replace(/\n/g, "<br>")}</p>
        </div>
        <div class="note-act">
          <button class="btn mini" data-act="toggle" data-id="${esc(n.id)}">${n.status === "resolved" ? "重新開啟" : "標為已解決"}</button>
          <button class="btn mini" data-act="goto" data-id="${esc(n.id)}">看計算</button>
          <button class="btn mini" data-act="del" data-id="${esc(n.id)}">刪除</button>
        </div>
      </div>`).join("") : `<p class="empty">${notesState.filter === "open" ? "沒有未解決的註記。" : "沒有註記。"}</p>`}</div>
    <p class="muted small">註記存在這台電腦的瀏覽器，並會放進「複製分享連結」；要給組員也可以「匯出註記」傳檔案。外掛匯出的結果 JSON 裡的註記會自動併入。</p>`;

  $("#n-add").onclick = () => {
    const text = $("#n-text").value.trim(); if (!text) return toast("請輸入註記內容");
    const sel = $("#n-target"), who = $("#n-author").value.trim();
    if (!who) { visitor.open(); return toast("請先輸入你的名字，註記會顯示是誰寫的"); }
    const t = nowText();
    notesState.list.unshift({ id: (crypto.randomUUID ? crypto.randomUUID() : String(Date.now())).replace(/-/g, ""), target: sel.value, target_label: sel.selectedOptions[0].textContent, author: who, text, status: "open", created_at: t, updated_at: t, deleted: false });
    saveNotes(); toast("已新增註記");
  };
  $("#n-filter").onclick = (e) => { const b = e.target.closest(".tab"); if (b) { notesState.filter = b.dataset.f; renderNotes(); } };
  $("#n-export").onclick = () => {
    const a = Object.assign(document.createElement("a"), { href: URL.createObjectURL(new Blob([JSON.stringify({ format: "lebr-notes", model: state.data.model, notes: notesState.list }, null, 2)], { type: "application/json" })), download: "lebr_notes.json" });
    a.click(); URL.revokeObjectURL(a.href);
  };
  $("#n-import-file").onchange = async (e) => {
    try {
      const o = JSON.parse(await e.target.files[0].text());
      const arr = Array.isArray(o) ? o : o.notes || o.web_notes || [];
      notesState.list = mergeNotes(notesState.list, arr); saveNotes(); toast(`已併入 ${arr.length} 則註記`);
    } catch (err) { toast("無法讀取註記檔"); }
    e.target.value = "";
  };
  box.querySelectorAll("[data-act]").forEach((b) => (b.onclick = () => {
    const n = notesState.list.find((x) => x.id === b.dataset.id); if (!n) return;
    if (b.dataset.act === "goto") return gotoTarget(n.target);
    if (b.dataset.act === "del") { if (!confirm("刪除這則註記？")) return; n.deleted = true; }
    else n.status = n.status === "resolved" ? "open" : "resolved";
    n.updated_at = nowText(); saveNotes();
  }));
}

function bindNoteButtons(root) {
  $$(".note-add", root).forEach((b) => (b.onclick = (e) => {
    e.preventDefault();
    showView("notes");
    const sel = $("#n-target");
    if (![...sel.options].some((o) => o.value === b.dataset.target)) sel.add(new Option(b.dataset.label, b.dataset.target));
    sel.value = b.dataset.target;
    $("#n-text").focus();
  }));
}
function gotoTarget(target) {
  showView("detail");
  const el = $(`.note-count[data-target="${CSS.escape(target)}"]`);
  if (!el) return toast("這則註記的對象不在計算詳情裡");
  let p = el.closest("details");
  while (p) { p.open = true; p = p.parentElement.closest("details"); }
  el.closest("details").scrollIntoView({ behavior: "smooth", block: "center" });
}

// ================================================================ 接上 app.js

const _renderAll = renderAll;
renderAll = function () {
  _renderAll();
  if (state.data && state._notesFor !== state.raw) { state._notesFor = state.raw; loadNotes(); }
  if (!state.data) { notesState.list = []; updateNoteCount(); }
  if (fromEstimate()) $("#proj-sub").textContent = $("#proj-sub").textContent.replace("標準公式（式 1～7）", "BIM 模型估算（外掛 LEBR_CFs）");
  renderDetail(); renderNotes();
};
renderDetail(); renderNotes();

// 左下使用者卡：顯示訪客名字，點了可以輸入或更換；換名字後註記人欄位跟著更新
function renderVisitorCard() {
  const n = visitor.name();
  $("#user-avatar").textContent = n ? n.slice(0, 1) : "訪";
  $("#user-name").textContent = n || "訪客";
  $("#user-sub").textContent = n ? "點此更換名字" : "點此輸入名字";
  const a = $("#n-author"); if (a) a.value = n;
}
$("#user-card").addEventListener("click", () => visitor.open());
addEventListener("visitorchange", renderVisitorCard);
renderVisitorCard();
