// LEBR 碳排圖台：匯出報告書（疊在 app.js、detail.js 之上）
// 依手冊 p.24 BIM 模型估算要求的三份文件（①估算模型說明、②數量估算報告、③地上層與地下層碳排報告）
// 加上計算過程、註記、評定申請應備文件與法規依據，產生可列印成 PDF 的 HTML。
// 外掛結果 JSON（含等級、表 6-2）也可以匯出，章節與外掛「匯出報告書…」相同。

const REPORT_DOCS = [
  ["低碳建築標示評定申請書（2025 年版，單棟或多棟）", "本報告書可作為計算附件草稿"],
  ["低碳建築標示評定文件申請表", "115 年 7 月版"],
  ["申請人切結書", ""],
  ["設計人切結書", ""],
  ["委任第三方建築師切結文件", "委任第三方建築師時"],
  ["評定報告授權書", ""],
  ["申請人代表授權書", "由代表人申請時"],
  ["估算模型說明、鋼筋與混凝土數量估算報告書、主結構碳排報告", "採 BIM 模型估算時（手冊 p.24）"],
  ["結構計算書（I、SaD、Fu、L、D0、Rs 依據）", "採標準計算法（式 4）時"],
  ["評定意見回覆表、變更差異說明表", "審查補正或設計變更時"],
];

const REPORT_CSS = `
:root{--ink:#1F1F1F;--quiet:#6B6B6B;--line:#E2DFD8;--page:#F6F5F2;--accent:#1a73ff;--soft:#E8F0FF;--warn:#FFF4D6;--warnline:#E8B949}
body{font-family:'Microsoft JhengHei UI','Microsoft JhengHei','PingFang TC','Noto Sans TC',sans-serif;color:var(--ink);background:#fff;max-width:980px;margin:24px auto;padding:0 24px;font-size:13px;line-height:1.6}
h1{font-size:28px;margin:6px 0}h2{font-size:17px;border-left:4px solid var(--accent);padding-left:10px;margin:28px 0 10px}h3{font-size:14px;margin:18px 0 8px}
.bar{position:sticky;top:0;display:flex;gap:8px;justify-content:flex-end;padding:8px 0;background:#fff;border-bottom:1px solid var(--line);z-index:2}
.bar button{font:inherit;padding:6px 14px;border-radius:999px;border:1px solid var(--line);background:#fff;cursor:pointer}.bar button.p{background:var(--accent);color:#fff;border-color:var(--accent)}
.bar span{margin-right:auto;color:var(--quiet);font-size:12px;align-self:center}
.cover{border:1px solid var(--line);border-radius:8px;padding:24px;background:var(--page)}.kicker{color:var(--accent);font-weight:bold;letter-spacing:1px}.sub{color:var(--quiet)}
.hero{display:inline-block;margin:16px 0;background:var(--soft);border:1px solid var(--accent);border-radius:8px;padding:10px 18px}.hero .big{font-size:30px;font-weight:bold;color:var(--accent)}
.cap,.small{color:var(--quiet);font-size:12px}
.warn{background:var(--warn);border:1px solid var(--warnline);border-radius:6px;padding:8px 12px;margin:8px 0}
table{border-collapse:collapse;width:100%;margin:6px 0}th,td{border-bottom:1px solid #EEEBE5;padding:5px 8px;text-align:left;vertical-align:top}th{background:#F1EFEA;font-weight:600}
.kv th{width:30%}.kv td,.kv th{border:1px solid var(--line)}.kv td[contenteditable]{background:#FFFDF5}td.num,th.num{text-align:right;font-variant-numeric:tabular-nums;white-space:nowrap}
tr.total td{font-weight:bold;border-top:2px solid var(--line)}
.cards{display:grid;grid-template-columns:repeat(4,1fr);gap:10px}.card{border:1px solid var(--line);border-radius:8px;padding:10px 12px}.card.hl{background:var(--soft);border-color:var(--accent)}.card .v{font-size:20px;font-weight:600}
.step{border:1px solid var(--line);border-radius:8px;padding:10px 12px;margin:8px 0;page-break-inside:avoid}.step .head{display:flex;gap:8px;align-items:center}.step .val{margin-left:auto;font-weight:600}
pre{white-space:pre-wrap;background:#FAF9F7;border:1px solid #EEEBE5;border-radius:6px;padding:6px 8px;margin:4px 0;font-family:Consolas,'Microsoft JhengHei UI',monospace;font-size:12px}
.tag{display:inline-block;font-size:11px;padding:0 6px;border-radius:4px;background:#E3F1EA;color:#2E7D5B;white-space:nowrap}.tag.est{background:var(--warn);color:#8a6300}.tag.mix{background:#EEF1F4;color:#44515e}
.params td,.params th{font-size:12px}.check td.box{width:24px;font-size:16px}
.sign{display:flex;justify-content:space-between;margin-top:40px;gap:16px}footer{margin-top:30px;color:var(--quiet);font-size:11px;text-align:center}
@media print{.bar{display:none}body{margin:0;max-width:none}.cover{page-break-after:always}h2{page-break-after:avoid}@page{size:A4;margin:16mm}}`;

const rn = (v, d = 0) => (v == null || isNaN(v) ? "—" : Number(v).toLocaleString("zh-TW", { maximumFractionDigits: d, minimumFractionDigits: d }));
const rtag = (b) => `<span class="tag ${b === "estimated" ? "est" : b === "mixed" ? "mix" : ""}">${(BASIS[b] || BASIS.estimated)[0]}</span>`;
const kv = (k, v, edit) => `<tr><th>${esc(k)}</th><td${edit ? ' contenteditable="true"' : ""}>${v ? esc(v) : "&nbsp;"}</td></tr>`;
const cardR = (cap, v, sub, hl) => `<div class="card${hl ? " hl" : ""}"><div class="cap">${esc(cap)}</div><div class="v">${esc(v)}</div><div class="small">${esc(sub || "")}</div></div>`;

function buildReport() {
  const d = state.data, raw = state.raw || {}, c = state.calc, b = d.building || {}, m = d.model || {};
  const plugin = Array.isArray(raw.calculation_steps) && raw.result && "grade" in raw.result;
  const bim = !std();
  const res = d.result || {};
  const above = bim ? c.above : res.above?.CFs, below = bim ? c.below : res.below?.CFs_prime;
  const draft = m.db_review_status && m.db_review_status !== "reviewed";
  const method = isStdMethod() ? "標準計算法（手冊式 1～7），屬估算" : "BIM 模型估算（模型數量 × 附錄一，手冊 p.24），屬精算";
  const h = [];

  h.push(`<div class="bar"><span>灰底黃色欄位可直接點選填寫，填完再列印。</span><button onclick="window.print()" class="p">列印／存成 PDF</button><button id="dl">下載 HTML</button></div>`);
  h.push(`<section class="cover"><div class="kicker">LEBR 低碳（低蘊含碳）建築評估</div>
    <h1>建築蘊含碳排評估報告書</h1><div class="sub">設計階段自評｜依內政部建築研究所《低碳（低蘊含碳）建築評估手冊》${esc(m.manual_version || "2025")} 年版</div>
    ${plugin
      ? `<div class="hero"><div class="cap">LEBR 等級</div><div class="big">${esc(raw.result.grade ? raw.result.grade + " 級" : "—")}</div><div>減碳率 CFR ${raw.result.CFR != null ? (raw.result.CFR * 100).toFixed(2) + "%" : "—"}</div></div>`
      : `<div class="hero"><div class="cap">地上層主結構碳排 CFs</div><div class="big">${rn(above)} kgCO₂e</div><div>${b.AFu ? "單位面積 " + rn(above / b.AFu, 1) + " kgCO₂e/m²" : ""}</div></div>`}
    ${draft ? `<div class="warn">資料庫尚未完成人工核對（${esc(m.db_review_status)}），本報告書僅供設計階段參考，不等於標章評定。</div>` : ""}
    <p class="small">本報告書整理評定申請所需的計算資料，可作為「低碳建築標示評定申請書（2025 年版）」之計算附件草稿。正式評定由內政部指定之評定專業機構（財團法人台灣建築中心）審查。</p></section>`);

  // 一、基本資料
  h.push(`<h2>一、建築物基本資料</h2><table class="kv">
    ${kv("建案名稱", "", true)}${kv("建築地點", "", true)}${kv("申請人（起造人）", "", true)}${kv("設計人（建築師）", "", true)}
    ${kv("模型／資料來源", m.name || state.source)}${kv("匯出時間", m.exported_at)}
    ${kv("地上／地下樓層數", `${b.S ?? "—"} 層／${b.Sb ?? "—"} 層`)}${kv("地上樓地板面積 AFu", b.AFu != null ? rn(b.AFu, 2) + " m²" : "")}${kv("地下樓地板面積 AFb", b.AFb != null ? rn(b.AFb, 2) + " m²" : "")}
    ${kv("主結構計算方式", method)}${kv("資料庫", `低碳建築評估手冊 ${m.manual_version || "?"} 版（${m.db_review_status || "?"}）`)}${kv("填表人", "", true)}</table>`);

  // 二、結果總表
  if (plugin) {
    const r = raw.result;
    h.push(`<h2>二、評估結果總表</h2><div class="cards">
      ${cardR("LEBR 等級", r.grade ? r.grade + " 級" : "—", "減碳率 CFR " + (r.CFR != null ? (r.CFR * 100).toFixed(2) + "%" : "—"), true)}
      ${cardR("蘊含碳排密度 ECI", rn(r.ECI, 2), "kgCO₂e/m²｜基準案 ECIs " + rn(r.ECIs, 2))}
      ${cardR("評估範疇蘊含碳排 EEC", rn(r.EEC), "kgCO₂e｜基準案 EECc " + rn(r.EECc))}
      ${cardR("減碳量 ΔCF", rn(r.delta_CF), "kgCO₂e｜TEC " + rn(r.TEC) + "（含地下層，只揭露）")}</div>
      ${raw.basis?.summary ? `<p class="small">計算方式：${esc(raw.basis.summary)}</p>` : ""}`);
    if (raw.grade_scale) h.push(`<h3>等級刻度（表 6-1）</h3><table><tr>${raw.grade_scale.map((g) => `<th>${esc(g.grade)} 級</th>`).join("")}</tr><tr>${raw.grade_scale.map((g) => `<td class="small">${esc(g.range || g.label || (g.eci != null ? "ECI ≦ " + rn(g.eci, 1) : ""))}</td>`).join("")}</tr></table>`);
    const sum = (arr) => arr.reduce((a, x) => a + (x.design || 0), 0);
    if (raw.by_stage) h.push(`<h2>三、生命週期階段碳排（表 6-2）</h2><table><tr><th>階段</th><th class="num">設計案 kgCO₂e</th><th class="num">基準案 kgCO₂e</th><th class="num">減碳量</th></tr>
      ${raw.by_stage.map((s) => `<tr><td>${esc(s.stage)}</td><td class="num">${rn(s.design)}</td><td class="num">${rn(s.baseline)}</td><td class="num">${rn(s.baseline - s.design)}</td></tr>`).join("")}
      <tr class="total"><td>合計</td><td class="num">${rn(sum(raw.by_stage))}</td><td class="num">${rn(raw.by_stage.reduce((a, x) => a + x.baseline, 0))}</td><td></td></tr></table>`);
    if (raw.by_component) h.push(`<h2>四、分項工程資材碳排（表 6-2）</h2><table><tr><th>分項工程</th><th class="num">設計案 kgCO₂e</th><th class="num">基準案 kgCO₂e</th><th class="num">減碳量</th></tr>
      ${raw.by_component.map((s) => `<tr><td>${esc(s.label)}</td><td class="num">${rn(s.design)}</td><td class="num">${rn(s.baseline)}</td><td class="num">${rn(s.baseline - s.design)}</td></tr>`).join("")}</table>`);
  } else {
    h.push(`<h2>二、主結構碳排總表</h2><div class="cards">
      ${cardR("地上層主結構 CFs", rn(above), "kgCO₂e", true)}
      ${cardR("地下層 CFs′", rn(below), "kgCO₂e｜只揭露，不計入減碳評估")}
      ${cardR("單位面積碳排", b.AFu ? rn(above / b.AFu, 1) : "—", "kgCO₂e/m²（CFs ÷ AFu）")}
      ${cardR("基準案 CFsc", res.baseline?.CFsc != null ? rn(res.baseline.CFsc) : "—", res.baseline?.CFsc != null ? res.baseline.method || "" : "選填，未提供")}</div>
      <p class="small">只計主結構時無法判定 LEBR 等級（需要七類構件齊全）。</p>`);
  }

  // 手冊 p.24 三份文件（BIM 數量法）
  if (bim) {
    const md = d.model_description || {};
    const rebar = md.rebar ? (md.rebar.mode === "ratio" ? `配筋率估算：${md.rebar.kg_per_m3 ?? "?"} kg/m³` : md.rebar.mode === "modeled" ? "模型中已建置鋼筋" : md.rebar.mode) : "";
    h.push(`<h2>三、① 估算模型說明</h2><table class="kv">${kv("建模軟體與版本", md.software)}${kv("建模範圍與深度", md.scope)}${kv("鋼筋估算方式", rebar)}</table>
      ${(md.assumptions || []).length ? `<h3>假設與限制</h3><ul>${md.assumptions.map((a) => `<li>${esc(a)}</li>`).join("")}</ul>` : ""}`);

    const lv = groupBy(c.rows, (r) => r.zone + "|" + r.level + "|" + r.category);
    h.push(`<h2>四、② 鋼筋、混凝土數量估算報告</h2><table><tr><th>範圍</th><th>樓層</th><th>類別</th><th class="num">混凝土 m³</th><th class="num">鋼筋 kg</th></tr>
      ${lv.map((g) => { const [z, l, k] = g.key.split("|"); return `<tr><td>${ZONE[z]}</td><td>${esc(l)}</td><td>${esc(CAT[k] || k)}</td><td class="num">${rn(g.qty.m3 || 0, 2)}</td><td class="num">${rn(g.qty.kg || 0, 1)}</td></tr>`; }).join("")}
      <tr class="total"><td colspan="3">合計</td><td class="num">${rn(c.rows.filter((r) => r.unit !== "kg").reduce((a, r) => a + r.quantity, 0), 2)}</td><td class="num">${rn(c.rows.filter((r) => r.unit === "kg").reduce((a, r) => a + r.quantity, 0), 1)}</td></tr></table>`);

    h.push(`<h2>五、③ 依附錄一計算的主結構碳排報告</h2>`);
    ["above", "below"].forEach((z) => {
      const g = groupBy(c.rows.filter((r) => r.zone === z), (r) => r.material_id);
      h.push(`<h3>${ZONE[z]}主結構 ${z === "above" ? "CFs" : "CFs′"}</h3>${g.length ? `<table><tr><th>附錄一編號</th><th>材料</th><th>單位</th><th class="num">數量</th><th class="num">A1</th><th class="num">A2</th><th class="num">A3</th><th class="num">A4</th><th class="num">係數合計</th><th class="num">碳排 kgCO₂e</th><th class="num">頁碼</th></tr>
        ${g.map((x) => { const f = x.rows[0].f, u = x.rows[0].unit; return `<tr><td>${esc(x.key)}</td><td>${esc(f.name)}</td><td>${esc(u)}</td><td class="num">${rn(x.qty[u], 2)}</td>${["a1", "a2", "a3", "a4"].map((k) => `<td class="num">${rn(f[k] ?? 0, 3)}</td>`).join("")}<td class="num">${rn(f.total, 3)}</td><td class="num">${rn(x.carbon)}</td><td class="num">${f.source_page ?? "—"}</td></tr>`; }).join("")}
        <tr class="total"><td colspan="9">合計</td><td class="num">${rn(c[z])}</td><td></td></tr></table>` : `<p class="small">${ZONE[z]}沒有結構元件數量。</p>`}`);
    });
  }

  // 計算過程
  h.push(`<h2>六、計算過程</h2><p class="small">每一步列出手冊公式、代入數字與結果。「精算」：依模型實際數量或使用者提供的數值；「估算」：依手冊標準公式、推估值或預設值。</p>`);
  const steps = plugin ? raw.calculation_steps.map((s) => ({ ...s, params: (s.parameters || []).map((p) => [p.symbol, p.name, p.value, p.source, p.basis]) }))
    : bim ? groupBy(c.rows, (r) => r.zone + "|" + r.level).map((l) => {
      const [z, name] = l.key.split("|");
      const cc = l.rows.filter((r) => r.unit !== "kg"), rr = l.rows.filter((r) => r.unit === "kg");
      const term = (rs) => groupBy(rs, (r) => r.material_id).map((g) => `${rn(g.qty[g.rows[0].unit], 2)} ${g.rows[0].unit} × ${rn(g.rows[0].f.total, 2)}`).join(" + ") || "0";
      return { section: ZONE[z], title: `${name}　CFsn`, formula: "CFsn = An × L + Bn × M", substitution: `= ${term(cc)} + ${term(rr)}\n= ${rn(cc.reduce((a, r) => a + r.carbon, 0))} + ${rn(rr.reduce((a, r) => a + r.carbon, 0))} = ${rn(l.carbon)} kgCO₂e`, value: l.carbon, unit: "kgCO2e", basis: "precise", params: [] };
    }) : [{ title: "地上主結構碳排 CFs", formula: "CFs = AFu × C × W × LCCR × RN（C 依式 4）", substitution: `CFs = ${rn(above)} kgCO₂e（舊版外掛 JSON 沒有代入過程）`, value: above, unit: "kgCO2e", basis: "estimated", params: [] }];
  let sec = null;
  steps.forEach((s) => {
    if (s.section && s.section !== sec) { sec = s.section; h.push(`<h3>${esc(sec)}</h3>`); }
    const v = s.unit && !/kgCO2e$/.test(s.unit) ? rn(s.value, 2) + " " + s.unit : rn(s.value) + " kgCO₂e";
    h.push(`<div class="step"><div class="head"><b>${esc(s.title)}</b>${s.manual_ref ? `<span class="small">${esc(s.manual_ref)}</span>` : ""}${rtag(s.basis)}<span class="val">${v}</span></div>
      <div class="small">手冊公式</div><pre>${esc(s.formula)}</pre>${s.substitution ? `<div class="small">代入</div><pre>${esc(s.substitution)}</pre>` : ""}
      ${s.basis_note ? `<div class="small">${esc(s.basis_note)}</div>` : ""}
      ${s.params?.length ? `<table class="params"><tr><th>符號</th><th>參數／係數</th><th class="num">數值</th><th>來源</th><th>方式</th></tr>${s.params.map(([sy, n, v2, src, bb]) => `<tr><td>${esc(sy)}</td><td>${esc(n)}</td><td class="num">${esc(v2)}</td><td>${esc(src)}</td><td>${rtag(bb)}</td></tr>`).join("")}</table>` : ""}</div>`);
  });

  // 提醒、註記
  const issues = (d.issues || raw.issues || []);
  h.push(`<h2>七、提醒與查核</h2>${issues.length ? `<table><tr><th>層級</th><th>內容</th></tr>${issues.map((i) => `<tr><td>${esc((LEVEL[i.level] || [i.level])[0])}</td><td>${esc(i.message)}</td></tr>`).join("")}</table>` : `<p class="small">無。</p>`}`);
  const notes = liveNotes();
  h.push(`<h2>八、審閱註記</h2>${notes.length ? `<table><tr><th>狀態</th><th>對象</th><th>內容</th><th>註記人</th><th>時間</th></tr>${notes.map((n) => `<tr><td>${n.status === "resolved" ? "已解決" : "未解決"}</td><td>${esc(n.target_label || n.target)}</td><td>${esc(n.text)}</td><td>${esc(n.author)}</td><td>${esc(String(n.updated_at || n.created_at || "").replace("T", " ").slice(0, 16))}</td></tr>`).join("")}</table>` : `<p class="small">無。</p>`}`);

  // 文件、法規、簽章
  h.push(`<h2>九、評定申請應備文件檢核</h2><p class="small">依台灣建築中心低碳建築標示（LEBR）評定文件下載頁所列表件；勾選後隨申請書送件。</p>
    <table class="check">${REPORT_DOCS.map(([a, bb]) => `<tr><td class="box">☐</td><td>${esc(a)}</td><td class="small">${esc(bb)}</td></tr>`).join("")}</table>
    <h3>法規依據與適用</h3><ul class="small">
    <li>《低碳（低蘊含碳）建築評估手冊》2025 年版，內政部建築研究所函頒，自 115 年 7 月 1 日起實施（2023 年版同日停止適用）。</li>
    <li>「建築蘊含碳排標示申請審核認可及使用作業要點」，內政部訂定，自 113 年 7 月 1 日生效；採指定評定專業機構（財團法人台灣建築中心）辦理。</li>
    <li>116 年 7 月 1 日起，公有新建 G-1（金融證券）、G-2（辦公場所）類建築物，申請綠建築標章及建築能效評估時須併同申請低碳建築標示，等級至少 2 級；119 年起至少 1 級。其他類別依主管機關公告期程。</li></ul>
    <div class="sign"><div>計算人：＿＿＿＿＿＿＿＿</div><div>設計人（建築師）簽章：＿＿＿＿＿＿＿＿</div><div>日期：＿＿＿＿＿＿＿＿</div></div>
    <footer>由 LEBR 碳排圖台產生｜${esc(new Date().toLocaleString("sv-SE").slice(0, 16))}</footer>`);

  const title = "低碳建築評估報告書－" + (m.name || "未命名");
  return `<!doctype html><html lang="zh-Hant"><head><meta charset="utf-8"><title>${esc(title)}</title><style>${REPORT_CSS}</style></head><body>${h.join("\n")}
<script>var dl=document.getElementById("dl");if(dl)dl.onclick=function(){var b=document.querySelector(".bar");b.remove();var s=document.querySelector("script");var html="<!doctype html>"+document.documentElement.outerHTML;document.body.prepend(b);var a=document.createElement("a");a.href=URL.createObjectURL(new Blob([html],{type:"text/html"}));a.download=${JSON.stringify(title + ".html")};a.click();};<\/script></body></html>`;
}

function exportReport() {
  if (!state.data) return toast("尚無資料可匯出");
  const url = URL.createObjectURL(new Blob([buildReport()], { type: "text/html;charset=utf-8" }));
  const w = window.open(url, "_blank");
  if (!w) {
    const a = Object.assign(document.createElement("a"), { href: url, download: "LEBR_報告書.html" });
    a.click(); toast("瀏覽器擋住新分頁，已改為下載報告書");
  }
}

// 版頭工具列加按鈕
(() => {
  const btn = Object.assign(document.createElement("button"), { className: "btn primary", id: "btn-report", textContent: "匯出報告書" });
  btn.onclick = exportReport;
  $("#btn-share").before(btn);
})();
