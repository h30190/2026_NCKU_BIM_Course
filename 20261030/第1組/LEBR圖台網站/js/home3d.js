// LEBR 碳排圖台：3D 首頁（effect 3：藍圖線框＋流光粒子）
// 模型是示意幾何（依樓層數自動產生），點構件時顯示該樓層、該類別的計算結果。
// 資料：預設為示範資料（數量由模型幾何算出 × 附錄一係數）；可載入外掛輸出的 JSON。
// 「進入計算結果」會把目前的資料用 #d= 帶到 dashboard.html（格式見 DATA_FORMAT.md）。

const CAT = {
  column: { label: "柱", en: "Column", color: "#6aa5ff", ratio: 200 },
  beam: { label: "梁", en: "Beam", color: "#5ac8fa", ratio: 160 },
  slab: { label: "樓板", en: "Slab", color: "#4d7bd6", ratio: 90 },
  wall: { label: "結構牆", en: "Wall", color: "#ffb340", ratio: 110 },
};
const ZONE = { above: "地上層", below: "地下層" };
const FACTORS = [
  { id: "PLCC-042", name: "預拌混凝土（3000psi）", unit: "m3", a1: 4.89, a2: 17.95, a3: 300.34, a4: 4.57, total: 327.75, source_page: 53 },
  { id: "PLCC-003", name: "鋼筋及鐵件", unit: "kg", a1: null, a2: null, a3: 0.168, a4: 0.017, total: 1.15, source_page: 52 },
];
const NX = 4, NY = 3, BAY = 6, FH = 3.6;
const W0 = NX * BAY, D0 = NY * BAY;

const $ = (s, r = document) => r.querySelector(s);
const $$ = (s, r = document) => [...r.querySelectorAll(s)];
const esc = (s) => String(s ?? "").replace(/[&<>"]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;" })[c]);
const num = (v, d = 0) => (v == null || isNaN(v) ? "—" : Number(v).toLocaleString("zh-TW", { maximumFractionDigits: d, minimumFractionDigits: 0 }));
const pct = (x) => (x * 100).toFixed(1) + "%";
const tons = (kg) => num(kg / 1000, 1);

function toast(msg, ms = 2800) {
  const t = $("#toast");
  t.textContent = msg; t.hidden = false;
  clearTimeout(toast.t); toast.t = setTimeout(() => (t.hidden = true), ms);
}

// ================================================================ 資料

/** 依樓層清單產生示意幾何：每層的柱往上一層、梁與樓板在該層標高（同 Revit 參考樓層）。 */
function buildElements(levels) {
  const els = [];
  const cx = W0 / 2, cz = D0 / 2;
  const add = (cat, lv, idx, box) => {
    const vol = box[3] * box[4] * box[5];
    els.push({ id: `${lv.name}-${CAT[cat].en}-${String(idx).padStart(2, "0")}`, cat, level: lv.name, zone: lv.zone, li: lv.i, box, vol });
  };
  levels.forEach((lv, k) => {
    const y = lv.y, top = k === levels.length - 1, bottom = k === 0;
    let n = 0;
    if (!top) {
      for (let i = 0; i <= NX; i++) for (let j = 0; j <= NY; j++) add("column", lv, ++n, [i * BAY - cx, y + FH / 2, j * BAY - cz, 0.6, FH, 0.6]);
    }
    if (bottom && lv.zone === "below") {
      add("slab", lv, 1, [0, y - 0.3, 0, W0 + 0.6, 0.6, D0 + 0.6]); // 筏基
    } else {
      n = 0;
      for (let i = 0; i < NX; i++) for (let j = 0; j <= NY; j++) add("beam", lv, ++n, [i * BAY + BAY / 2 - cx, y - 0.3, j * BAY - cz, BAY - 0.6, 0.6, 0.4]);
      for (let j = 0; j < NY; j++) for (let i = 0; i <= NX; i++) add("beam", lv, ++n, [i * BAY - cx, y - 0.3, j * BAY + BAY / 2 - cz, 0.4, 0.6, BAY - 0.6]);
      add("slab", lv, 1, [0, y + 0.075, 0, W0 + 0.6, 0.15, D0 + 0.6]);
    }
    if (!top) {
      n = 0;
      if (lv.zone === "below") {
        // 地下室外牆（擋土牆）
        add("wall", lv, ++n, [0, y + FH / 2, -cz - 0.45, W0 + 1.2, FH, 0.3]);
        add("wall", lv, ++n, [0, y + FH / 2, cz + 0.45, W0 + 1.2, FH, 0.3]);
        add("wall", lv, ++n, [-cx - 0.45, y + FH / 2, 0, 0.3, FH, D0]);
        add("wall", lv, ++n, [cx + 0.45, y + FH / 2, 0, 0.3, FH, D0]);
      } else {
        // 電梯核心的剪力牆
        add("wall", lv, ++n, [BAY / 2 - 1.2, y + FH / 2, -1.5, 0.25, FH - 0.6, 3]);
        add("wall", lv, ++n, [BAY / 2 + 1.2, y + FH / 2, -1.5, 0.25, FH - 0.6, 3]);
      }
    }
  });
  return els;
}

const DEMO_LEVELS = [
  { name: "B1", zone: "below" }, { name: "1F", zone: "above" }, { name: "2F", zone: "above" },
  { name: "3F", zone: "above" }, { name: "4F", zone: "above" }, { name: "RF", zone: "above" },
];

/** 示範資料：數量由示意幾何算出，鋼筋以配筋率估算，格式同 lebr-structure-result。 */
function demoData() {
  const levels = layoutLevels(DEMO_LEVELS);
  const els = buildElements(levels);
  const agg = new Map();
  for (const e of els) {
    const k = e.cat + "|" + e.level;
    const g = agg.get(k) || { category: e.cat, level: e.level, zone: e.zone, m3: 0, kg: 0 };
    g.m3 += e.vol; g.kg += e.vol * CAT[e.cat].ratio;
    agg.set(k, g);
  }
  const quantities = [];
  let id = 1000;
  for (const g of agg.values()) {
    quantities.push({ element_id: id++, category: g.category, zone: g.zone, level: g.level, material_id: "PLCC-042", unit: "m3", quantity: round(g.m3, 3) });
    quantities.push({ element_id: id++, category: g.category, zone: g.zone, level: g.level, material_id: "PLCC-003", unit: "kg", quantity: round(g.kg, 1) });
  }
  const sumZone = (z) => quantities.filter((q) => q.zone === z).reduce((a, q) => a + q.quantity * factor(q.material_id).total, 0);
  const afFloor = W0 * D0;
  return {
    format: "lebr-structure-result",
    version: 1,
    model: { name: "示範模型（首頁示意）", exported_at: "2026-10-29 12:00", manual_version: "2025", db_review_status: "draft" },
    method: "bim_quantity",
    model_description: {
      software: "首頁示範資料",
      scope: "柱、梁、樓板、結構牆（地上 1F～RF、地下 B1）",
      assumptions: ["數量由首頁示意幾何計算", "鋼筋以配筋率估算：柱 200、梁 160、樓板 90、結構牆 110 kg/m³", "混凝土 PLCC-042、鋼筋 PLCC-003（外掛估算視窗的預設材料）"],
      rebar: { mode: "ratio", kg_per_m3: 140 },
    },
    building: { AFu: afFloor * 4, AFb: afFloor, S: 4, Sb: 1 },
    quantities,
    factors: FACTORS,
    result: { above: { CFs: round(sumZone("above"), 0) }, below: { CFs_prime: round(sumZone("below"), 0) }, baseline: { CFsc: null } },
    issues: [{ level: "info", message: "首頁示範資料，不是實際專案。" }],
    unmapped: [],
  };
}
const round = (v, d) => Math.round(v * 10 ** d) / 10 ** d;
const factor = (id) => state.factors.get(id) || FACTORS.find((f) => f.id === id) || { id, total: 0 };

/** 樓層排序並給示意標高：地下層由深到淺，地上層依名稱中的數字。 */
function layoutLevels(list) {
  const key = (n) => {
    const s = String(n).toUpperCase();
    if (/^(R|RF|ROOF|屋頂)/.test(s)) return 999;
    const m = s.match(/-?\d+(\.\d+)?/);
    return m ? parseFloat(m[0]) : 500;
  };
  const below = list.filter((l) => l.zone === "below").sort((a, b) => key(b.name) - key(a.name));
  const above = list.filter((l) => l.zone !== "below").sort((a, b) => key(a.name) - key(b.name));
  const out = [];
  below.forEach((l, i) => out.push({ ...l, zone: "below", y: -(below.length - i) * FH }));
  above.forEach((l, i) => out.push({ ...l, zone: "above", y: i * FH }));
  // 最上層只放梁與樓板；地上只有一層時補一個屋頂層，讓柱有地方接
  if (above.length === 1) out.push({ name: above[0].name + " 上方", zone: "above", y: FH, synthetic: true });
  out.forEach((l, i) => (l.i = i));
  return out;
}

/** 把各種輸入整理成：類別×樓層、樓層、地上地下合計。 */
function summarize(d) {
  const S = { groups: new Map(), levels: new Map(), above: 0, below: 0, kind: "full", note: "" };
  const lv = (name, zone) => {
    if (!S.levels.has(name)) S.levels.set(name, { name, zone, carbon: 0, m3: 0, kg: 0 });
    return S.levels.get(name);
  };
  if (d.format === "lebr-structure-result") {
    for (const q of d.quantities || []) {
      const f = factor(q.material_id);
      const c = (q.quantity || 0) * (f.total || 0);
      const k = q.category + "|" + q.level;
      const g = S.groups.get(k) || { cat: q.category, level: q.level, zone: q.zone, m3: 0, kg: 0, cC: 0, cR: 0, mats: new Set() };
      if (q.unit === "kg") { g.kg += q.quantity; g.cR += c; } else { g.m3 += q.quantity; g.cC += c; }
      g.mats.add(q.material_id);
      S.groups.set(k, g);
      const L = lv(q.level, q.zone);
      L.carbon += c; if (q.unit === "kg") L.kg += q.quantity; else L.m3 += q.quantity;
      if (q.zone === "below") S.below += c; else S.above += c;
    }
  } else if (Array.isArray(d.levels) && d.result && "cfs" in d.result) {
    // 外掛的估算紀錄（LEBR_EstimateJson）：只有各樓層合計
    S.kind = "levels";
    S.note = "估算紀錄只有各樓層合計，沒有分柱、梁、樓板、牆。";
    for (const l of d.levels) {
      const zone = (l.below_ground ?? (l.elevation_m < 0)) ? "below" : "above";
      const L = lv(l.name, zone);
      L.carbon += l.cfs_n || 0; L.m3 += l.concrete_m3 || 0; L.kg += l.rebar_kg || 0;
      L.cC = l.cfc; L.cR = l.cfr; L.elev = l.elevation_m;
    }
    S.above = d.result.above || 0; S.below = d.result.below || 0;
  } else {
    S.kind = "total";
    S.note = "標準公式結果沒有各樓層、各構件的數量，只能顯示整棟主結構 CFs。";
    S.above = d.result?.above?.CFs ?? 0; S.below = d.result?.below?.CFs_prime ?? 0;
  }
  return S;
}

// 外掛目前的標準公式結果（DATA_FORMAT.md「接受的格式」第一列）
const isLegacy = (o) => o.project && o.result && Array.isArray(o.by_component);
function fromLegacy(o) {
  const p = o.project, st = o.by_component.find((c) => c.key === "structure") || {};
  const model = { name: String(p.source ?? "").replace(/^目前模型：/, ""), exported_at: String(o.calculated_at ?? "").replace("T", " ").slice(0, 16), manual_version: o.database?.manual_version, db_review_status: o.database?.review_status };
  // 外掛勾選「使用估算」且估算視窗按過「計算並儲存」時，structure.bim 有各品類 × 樓層的數量，3D 依此分樓層、分構件
  const s = o.structure, b = s?.bim;
  if (s?.method === "bim_quantity" && Array.isArray(b?.quantities) && b.quantities.length && Array.isArray(b.factors)) {
    return {
      format: "lebr-structure-result",
      method: "bim_quantity",
      model,
      model_description: b.model_description || {},
      building: { AFu: p.AFu, AFb: p.AFb, S: p.floors, Sb: p.basement_floors },
      quantities: b.quantities,
      factors: b.factors,
      result: { above: { CFs: b.totals?.above ?? s.design_CFs }, below: { CFs_prime: b.totals?.below ?? o.underground?.structure }, baseline: { CFsc: s.baseline_CFsc } },
      raw: o,
    };
  }
  return {
    method: "standard_formula",
    model,
    building: { AFu: p.AFu, AFb: p.AFb, S: p.floors, Sb: p.basement_floors },
    quantities: [], factors: [],
    result: { above: { CFs: st.design }, below: { CFs_prime: o.underground?.structure }, baseline: { CFsc: st.baseline } },
    raw: o,
  };
}

const state = {
  data: null, raw: null, source: "示範資料", summary: null, factors: new Map(),
  levels: [], els: [], filter: new Set(Object.keys(CAT)), sel: null, hover: null,
};

function useData(d, label, raw) {
  state.data = d; state.raw = raw ?? d; state.source = label;
  state.factors = new Map((d.factors || []).map((f) => [f.id, f]));
  state.summary = summarize(d);
  let levels;
  if (state.summary.levels.size) {
    levels = [...state.summary.levels.values()].map((l) => ({ name: l.name, zone: l.zone }));
  } else {
    const s = Math.max(1, Math.min(12, Math.round(d.building?.S || 4))), sb = Math.max(0, Math.min(3, Math.round(d.building?.Sb || 0)));
    levels = [];
    for (let i = sb; i >= 1; i--) levels.push({ name: "B" + i, zone: "below" });
    for (let i = 1; i <= s; i++) levels.push({ name: i + "F", zone: "above" });
    levels.push({ name: "RF", zone: "above" });
  }
  if (levels.length > 16) toast("Too many levels; the model shows the first 16");
  state.levels = layoutLevels(levels.slice(0, 16));
  state.els = buildElements(state.levels);
  // 有分構件數量時，只畫模型裡有數量的構件類別（例如沒有結構牆就不畫電梯核心牆）
  if (state.summary.kind === "full") {
    const cats = new Set([...state.summary.groups.values()].map((g) => g.cat));
    state.els = state.els.filter((e) => cats.has(e.cat));
  }
  state.sel = null;
  buildScene();
  renderStats();
  renderSource();
  closePanel();
}

// ================================================================ 3D

const stage = $("#stage");
const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: true });
renderer.setPixelRatio(Math.min(devicePixelRatio, 2));
renderer.setClearColor(0x000000, 0);
stage.appendChild(renderer.domElement);
const scene = new THREE.Scene();
const camera = new THREE.PerspectiveCamera(34, 1, 0.1, 600);
const root = new THREE.Group();
scene.add(root);
const unitBox = new THREE.BoxGeometry(1, 1, 1);
const unitEdges = new THREE.EdgesGeometry(unitBox);
const sprite = (() => {
  const c = document.createElement("canvas"); c.width = c.height = 64;
  const x = c.getContext("2d"), g = x.createRadialGradient(32, 32, 0, 32, 32, 32);
  g.addColorStop(0, "rgba(255,255,255,1)"); g.addColorStop(0.35, "rgba(255,255,255,.55)"); g.addColorStop(1, "rgba(255,255,255,0)");
  x.fillStyle = g; x.fillRect(0, 0, 64, 64);
  return new THREE.CanvasTexture(c);
})();

const view = { theta: 0.78, phi: 1.12, radius: 70, target: new THREE.Vector3(0, 6, 0), explode: 0, explodeTo: 0, idle: 0 };
const scene3 = { levelGroups: [], picks: [], edgeMats: new Map(), flow: null, scan: null, grid: null, dust: null, height: 20 };

function buildScene() {
  root.clear();
  scene3.levelGroups = []; scene3.picks = []; scene3.edgeMats.clear();
  for (const lv of state.levels) {
    const g = new THREE.Group(); g.userData.level = lv; root.add(g); scene3.levelGroups.push(g);
  }
  for (const e of state.els) {
    const key = e.cat + "|" + e.level;
    let em = scene3.edgeMats.get(key);
    if (!em) {
      em = new THREE.LineBasicMaterial({ color: CAT[e.cat].color, transparent: true, opacity: baseEdgeOpacity(e.cat) });
      scene3.edgeMats.set(key, em);
    }
    const [x, y, z, sx, sy, sz] = e.box;
    const edges = new THREE.LineSegments(unitEdges, em);
    edges.scale.set(sx, sy, sz); edges.position.set(x, y, z);
    const fill = new THREE.Mesh(unitBox, new THREE.MeshBasicMaterial({ color: CAT[e.cat].color, transparent: true, opacity: 0, depthWrite: false }));
    fill.scale.copy(edges.scale); fill.position.copy(edges.position);
    fill.userData.el = e; e.fill = fill; e.edges = edges;
    const g = scene3.levelGroups[e.li];
    g.add(edges); g.add(fill);
    scene3.picks.push(fill);
  }
  const lo = state.levels[0].y, hi = state.levels[state.levels.length - 1].y;
  scene3.height = hi - lo;
  view.target.set(0, (lo + hi) / 2, 0);
  view.radius = Math.max(50, scene3.height * 2.2 + 26);

  // 地面格線
  const grid = new THREE.GridHelper(90, 45, 0x2a3d66, 0x1c2742);
  grid.material.transparent = true; grid.material.opacity = 0.55; grid.position.y = Math.min(0, lo) - 0.65;
  root.add(grid); scene3.grid = grid;

  // 沿柱、梁流動的粒子
  const flows = [];
  for (const e of state.els) {
    if (e.cat !== "column" && e.cat !== "beam") continue;
    const [, , , sx, sy, sz] = e.box;
    const axis = sy >= sx && sy >= sz ? 1 : sx >= sz ? 0 : 2;
    const len = [sx, sy, sz][axis];
    const n = Math.max(3, Math.round(len * 1.6));
    for (let k = 0; k < n; k++) flows.push({ e, axis, len, t: Math.random(), v: (0.05 + Math.random() * 0.12) * (Math.random() < 0.5 ? 1 : -1), j: [Math.random() - 0.5, Math.random() - 0.5, Math.random() - 0.5] });
  }
  const fg = new THREE.BufferGeometry();
  fg.setAttribute("position", new THREE.Float32BufferAttribute(new Float32Array(flows.length * 3), 3));
  fg.setAttribute("color", new THREE.Float32BufferAttribute(new Float32Array(flows.length * 3), 3));
  const flowPts = new THREE.Points(fg, new THREE.PointsMaterial({ size: 0.55, map: sprite, vertexColors: true, transparent: true, opacity: 0.95, depthWrite: false, blending: THREE.AdditiveBlending }));
  flowPts.frustumCulled = false;
  root.add(flowPts);
  scene3.flow = { pts: flowPts, list: flows };

  // 漂浮微粒
  const dust = [], dc = [];
  for (let k = 0; k < 2200; k++) { dust.push((Math.random() - 0.5) * 100, lo - 4 + Math.random() * (scene3.height + 26), (Math.random() - 0.5) * 100); dc.push(0.1, 0.17, 0.38); }
  const dg = new THREE.BufferGeometry();
  dg.setAttribute("position", new THREE.Float32BufferAttribute(dust, 3));
  dg.setAttribute("color", new THREE.Float32BufferAttribute(dc, 3));
  scene3.dust = new THREE.Points(dg, new THREE.PointsMaterial({ size: 0.5, map: sprite, vertexColors: true, transparent: true, opacity: 0.8, depthWrite: false, blending: THREE.AdditiveBlending }));
  root.add(scene3.dust);

  // 掃描平面
  const pw = W0 + 6, pd = D0 + 6;
  const plane = new THREE.Mesh(new THREE.PlaneGeometry(pw, pd), new THREE.MeshBasicMaterial({ color: 0x1a73ff, transparent: true, opacity: 0.1, side: THREE.DoubleSide, depthWrite: false }));
  plane.rotation.x = -Math.PI / 2;
  const frame = new THREE.LineSegments(new THREE.EdgesGeometry(new THREE.PlaneGeometry(pw, pd)), new THREE.LineBasicMaterial({ color: 0x8bb8ff, transparent: true, opacity: 0.8 }));
  frame.rotation.x = -Math.PI / 2;
  const scan = new THREE.Group(); scan.add(plane); scan.add(frame); root.add(scan);
  scene3.scan = { group: scan, idx: 0, clock: 0, corner: new THREE.Vector3(-pw / 2, 0, -pd / 2) };

  applyFilter();
  resize();
}

const baseEdgeOpacity = (cat) => (cat === "slab" ? 0.42 : cat === "wall" ? 0.6 : 0.78);

function levelOffset(i) {
  const lv = state.levels[i];
  if (!lv) return 0;
  const below = state.levels.filter((l) => l.zone === "below").length;
  return (i - below) * view.explode * 4 + (lv.zone === "below" ? -view.explode * 3 : 0);
}

function applyHighlight() {
  const sel = state.sel, hov = state.hover;
  for (const [key, m] of scene3.edgeMats) {
    const [cat, level] = key.split("|");
    const isSel = sel && sel.cat === cat && sel.level === level;
    const isLevel = sel && !isSel && sel.level === level;
    const isHov = hov && hov.cat === cat && hov.level === level;
    m.color.set(isSel ? "#ffffff" : CAT[cat].color);
    m.opacity = sel ? (isSel ? 1 : isLevel ? 0.75 : 0.18) : isHov ? 1 : baseEdgeOpacity(cat);
  }
  for (const e of state.els) {
    const isSel = sel && sel.cat === e.cat && sel.level === e.level;
    const isHov = hov && hov.cat === e.cat && hov.level === e.level;
    e.fill.material.opacity = sel && sel.el === e ? 0.55 : isSel ? 0.22 : isHov ? 0.16 : 0;
  }
}

function applyFilter() {
  for (const e of state.els) {
    const on = state.filter.has(e.cat);
    e.edges.visible = on; e.fill.visible = on;
  }
  renderFilters();
}

function resize() {
  const w = innerWidth, h = innerHeight;
  renderer.setSize(w, h);
  camera.aspect = w / h;
  // 桌機：模型放在右半邊；窄螢幕：模型往下移，讓出上方文字
  if (w > 1100) camera.setViewOffset(w, h, -w * 0.2, 0, w, h);
  else camera.setViewOffset(w, h, 0, -h * 0.16, w, h);
  camera.updateProjectionMatrix();
}
addEventListener("resize", resize);

function updateCamera() {
  const r = view.radius * (innerWidth < 720 ? 1.35 : 1);
  camera.position.set(
    view.target.x + r * Math.sin(view.phi) * Math.sin(view.theta),
    view.target.y + r * Math.cos(view.phi),
    view.target.z + r * Math.sin(view.phi) * Math.cos(view.theta),
  );
  camera.lookAt(view.target);
}

// ---------------------------------------------------------------- 互動
const pointer = { down: false, x: 0, y: 0, moved: 0, nx: 0, ny: 0, inside: false };
const ray = new THREE.Raycaster();
stage.addEventListener("pointerdown", (e) => {
  pointer.down = true; pointer.x = e.clientX; pointer.y = e.clientY; pointer.moved = 0;
  stage.setPointerCapture(e.pointerId);
});
stage.addEventListener("pointermove", (e) => {
  pointer.nx = (e.clientX / innerWidth) * 2 - 1; pointer.ny = -(e.clientY / innerHeight) * 2 + 1; pointer.inside = true;
  pointer.cx = e.clientX; pointer.cy = e.clientY;
  if (pointer.down) {
    const dx = e.clientX - pointer.x, dy = e.clientY - pointer.y;
    pointer.moved += Math.abs(dx) + Math.abs(dy);
    if (pointer.moved > 4) stage.classList.add("dragging");
    view.theta -= dx * 0.006; view.phi = Math.min(1.5, Math.max(0.35, view.phi - dy * 0.005));
    pointer.x = e.clientX; pointer.y = e.clientY; view.idle = 0;
  }
});
stage.addEventListener("pointerup", (e) => {
  pointer.down = false; stage.classList.remove("dragging");
  if (pointer.moved < 6) {
    const hit = pick(e.clientX, e.clientY);
    if (hit) select(hit.cat, hit.level, hit);
    else if (state.sel) closePanel();
  }
});
stage.addEventListener("pointerleave", () => { pointer.inside = false; setHover(null); });
stage.addEventListener("wheel", (e) => {
  e.preventDefault();
  view.radius = Math.min(170, Math.max(28, view.radius * (1 + Math.sign(e.deltaY) * 0.08)));
  view.idle = 0;
}, { passive: false });

function pick(cx, cy) {
  const v = new THREE.Vector2((cx / innerWidth) * 2 - 1, -(cy / innerHeight) * 2 + 1);
  ray.setFromCamera(v, camera);
  const hits = ray.intersectObjects(scene3.picks.filter((m) => m.visible), false);
  return hits.length ? hits[0].object.userData.el : null;
}

function setHover(el) {
  const prev = state.hover;
  state.hover = el;
  const tag = $("#hover-tag");
  if (el) {
    tag.hidden = false;
    tag.textContent = `${el.level} · ${CAT[el.cat].en} ${el.id}`;
    tag.style.left = pointer.cx + "px"; tag.style.top = pointer.cy + "px";
    stage.classList.add("pointing");
  } else {
    tag.hidden = true; stage.classList.remove("pointing");
  }
  if ((prev && prev.cat + prev.level) !== (el && el.cat + el.level)) applyHighlight();
}

$("#btn-explode").addEventListener("click", (e) => {
  view.explodeTo = view.explodeTo > 0.5 ? 0 : 1;
  e.currentTarget.setAttribute("aria-pressed", view.explodeTo > 0.5);
  e.currentTarget.textContent = view.explodeTo > 0.5 ? "Assemble" : "Explode";
});
$("#btn-reset").addEventListener("click", () => {
  view.theta = 0.78; view.phi = 1.12; view.radius = Math.max(50, scene3.height * 2.2 + 26); view.idle = 0;
});

function renderFilters() {
  $("#filters").innerHTML = Object.entries(CAT).map(([k, c]) =>
    `<button class="fchip ${state.filter.has(k) ? "on" : ""}" data-cat="${k}" style="--c:${c.color}" aria-pressed="${state.filter.has(k)}"><i></i>${c.en}</button>`).join("");
}
$("#filters").addEventListener("click", (e) => {
  const b = e.target.closest(".fchip"); if (!b) return;
  const k = b.dataset.cat;
  if (state.filter.has(k) && state.filter.size > 1) state.filter.delete(k); else state.filter.add(k);
  applyFilter();
});

// ---------------------------------------------------------------- 動畫
const tmp = new THREE.Vector3();
let last = performance.now();
function frame(now) {
  const dt = Math.min(0.05, (now - last) / 1000); last = now;
  view.idle += dt;
  if (view.idle > 4 && !state.sel && !pointer.down) view.theta += dt * 0.08;
  view.explode += (view.explodeTo - view.explode) * Math.min(1, dt * 5);
  scene3.levelGroups.forEach((g, i) => (g.position.y = levelOffset(i)));
  updateCamera();

  // 流光粒子
  const f = scene3.flow;
  if (f) {
    const pos = f.pts.geometry.attributes.position.array, col = f.pts.geometry.attributes.color.array;
    const sel = state.sel;
    for (let k = 0; k < f.list.length; k++) {
      const p = f.list[k], e = p.e;
      p.t += p.v * dt * (6 / p.len);
      if (p.t > 1) p.t -= 1; else if (p.t < 0) p.t += 1;
      const [x, y, z, sx, sy, sz] = e.box;
      const off = [p.j[0] * sx * 0.9, p.j[1] * sy * 0.9, p.j[2] * sz * 0.9];
      off[p.axis] = (p.t - 0.5) * p.len;
      const visible = state.filter.has(e.cat);
      pos[k * 3] = x + off[0]; pos[k * 3 + 1] = visible ? y + off[1] + levelOffset(e.li) : -9999; pos[k * 3 + 2] = z + off[2];
      const hot = sel && sel.cat === e.cat && sel.level === e.level;
      const dim = sel && !hot;
      col[k * 3] = hot ? 1 : dim ? 0.12 : 0.35; col[k * 3 + 1] = hot ? 1 : dim ? 0.2 : 0.72; col[k * 3 + 2] = dim ? 0.4 : 1;
    }
    f.pts.geometry.attributes.position.needsUpdate = true;
    f.pts.geometry.attributes.color.needsUpdate = true;
  }

  // 掃描平面：逐層移動；選取構件時停在該層
  const sc = scene3.scan;
  if (sc) {
    sc.clock += dt;
    if (state.sel) sc.idx = state.levels.findIndex((l) => l.name === state.sel.level);
    else if (sc.clock > 2.6) { sc.clock = 0; sc.idx = (sc.idx + 1) % state.levels.length; }
    const lv = state.levels[Math.max(0, sc.idx)];
    const ty = lv.y + 1.6 + levelOffset(lv.i);
    sc.group.position.y += (ty - sc.group.position.y) * Math.min(1, dt * 4);
    tmp.copy(sc.corner); tmp.y = sc.group.position.y; tmp.project(camera);
    const tag = $("#scan-tag");
    const L = state.summary?.levels.get(lv.name);
    tag.hidden = !(tmp.z < 1);
    tag.style.left = ((tmp.x + 1) / 2) * innerWidth + 8 + "px";
    tag.style.top = ((1 - tmp.y) / 2) * innerHeight + "px";
    const text = `Scanning ${lv.name}${L ? `<b>${tons(L.carbon)} tCO₂e</b>` : ""}`;
    if (tag.dataset.t !== text) { tag.innerHTML = text; tag.dataset.t = text; }
  }

  // 滑鼠移入
  if (pointer.inside && !pointer.down) {
    const hit = pick(pointer.cx, pointer.cy);
    if (hit !== state.hover) setHover(hit);
    else if (hit) { const t = $("#hover-tag"); t.style.left = pointer.cx + "px"; t.style.top = pointer.cy + "px"; }
  }

  renderer.render(scene, camera);
  requestAnimationFrame(frame);
}

// ================================================================ 詳情面板

function select(cat, level, el) {
  state.sel = { cat, level, el: el || null };
  view.idle = 0;
  applyHighlight();
  renderPanel();
}
function closePanel() {
  state.sel = null;
  $("#comp-panel").hidden = true;
  applyHighlight();
}

function renderPanel() {
  const { cat, level, el } = state.sel;
  const S = state.summary, d = state.data;
  const lvInfo = state.levels.find((l) => l.name === level);
  const zone = lvInfo?.zone || "above";
  const zoneTotal = zone === "below" ? S.below : S.above;
  const L = S.levels.get(level);
  const g = S.groups.get(cat + "|" + level);
  const methodBadge = d.method === "standard_formula"
    ? `<span class="badge amber" title="未勾選「使用估算」：依手冊式 1～7 標準公式">估算｜標準公式</span>`
    : `<span class="badge" title="模型的混凝土、鋼筋數量 × 附錄一係數">精算｜BIM 模型數量</span>`;
  let body = "";

  if (S.kind === "full" && g) {
    const total = g.cC + g.cR;
    const conc = [...g.mats].map(factor).find((f) => f.unit === "m3");
    const reb = [...g.mats].map(factor).find((f) => f.unit === "kg");
    body += `
      <div><div class="big-num">${num(total)}<small>kgCO₂e</small></div><div class="muted small">${tons(total)} tCO₂e</div></div>
      <div class="share">
        <div class="muted small">占${ZONE[zone]}主結構 ${pct(zoneTotal ? total / zoneTotal : 0)}</div>
        <div class="bar-bg"><i style="width:${Math.min(100, zoneTotal ? (total / zoneTotal) * 100 : 0)}%"></i></div>
        <div class="muted small">占 ${esc(level)} 本層 ${pct(L && L.carbon ? total / L.carbon : 0)}</div>
      </div>
      <div class="info-grid">
        <div><small>混凝土 An</small><b>${num(g.m3, 2)} m³</b></div>
        <div><small>鋼筋 Bn</small><b>${num(g.kg, 0)} kg</b></div>
        <div><small>L｜${esc(conc?.id ?? "—")}</small><b>${num(conc?.total, 2)}</b><small>${esc(conc?.name ?? "")} kgCO₂e/m³</small></div>
        <div><small>M｜${esc(reb?.id ?? "—")}</small><b>${num(reb?.total, 2)}</b><small>${esc(reb?.name ?? "")} kgCO₂e/kg</small></div>
      </div>
      <div class="formula">
        <code>CFsn = An × L + Bn × M</code><br>
        = ${num(g.m3, 2)} × ${num(conc?.total, 2)} + ${num(g.kg, 0)} × ${num(reb?.total, 2)}<br>
        = ${num(g.cC)} + ${num(g.cR)} = <span class="res">${num(total)} kgCO₂e</span>
        <div class="muted small" style="margin-top:6px">手冊附錄一：L、M 取 P-LCC「總碳排量」（A1～A4）。</div>
      </div>`;
    if (el && d.model_description?.software === "首頁示範資料") {
      const ec = el.vol * (conc?.total || 0), er = el.vol * CAT[cat].ratio * (reb?.total || 0);
      body += `
      <details class="more" open><summary>這個構件　${esc(el.id)}</summary><div class="inner">
        <table class="mini-table">
          <tr><td>尺寸</td><td class="num">${num(el.box[3], 2)} × ${num(el.box[5], 2)} × ${num(el.box[4], 2)} m</td></tr>
          <tr><td>混凝土體積</td><td class="num">${num(el.vol, 3)} m³</td></tr>
          <tr><td>鋼筋（配筋率 ${CAT[cat].ratio} kg/m³）</td><td class="num">${num(el.vol * CAT[cat].ratio, 1)} kg</td></tr>
          <tr><td>碳排</td><td class="num">${num(ec)} + ${num(er)} = <b>${num(ec + er)}</b></td></tr>
        </table></div></details>`;
    }
    body += levelTable(level, cat);
    if (conc && conc.a3 != null) {
      body += `
      <details class="more"><summary>混凝土係數 A1～A4 分段</summary><div class="inner">
        <table class="mini-table">
          ${[["A1 原料開採", conc.a1], ["A2 原料運輸", conc.a2], ["A3 產品生產", conc.a3], ["A4 成品運輸", conc.a4]].map(([n, v]) => `<tr><td>${n}</td><td class="num">${num(v, 2)}</td></tr>`).join("")}
          <tr><td><b>總碳排量 L</b></td><td class="num"><b>${num(conc.total, 2)}</b></td></tr>
        </table><div class="muted small">kgCO₂e/m³，附錄一 p.${esc(conc.source_page ?? "")}</div></div></details>`;
    }
  } else if (S.kind === "full") {
    body += `<div class="hint">${esc(level)} 沒有${CAT[cat].label}的數量資料。</div>` + levelTable(level, cat);
  } else if (S.kind === "levels" && L) {
    body += `
      <div><div class="big-num">${num(L.carbon)}<small>kgCO₂e</small></div><div class="muted small">${esc(level)} 本層合計（${tons(L.carbon)} tCO₂e）</div></div>
      <div class="share"><div class="muted small">占${ZONE[zone]}主結構 ${pct(zoneTotal ? L.carbon / zoneTotal : 0)}</div><div class="bar-bg"><i style="width:${zoneTotal ? (L.carbon / zoneTotal) * 100 : 0}%"></i></div></div>
      <div class="info-grid"><div><small>混凝土 An</small><b>${num(L.m3, 2)} m³</b></div><div><small>鋼筋 Bn</small><b>${num(L.kg, 0)} kg</b></div></div>
      <div class="formula"><code>CFsn = An × L + Bn × M</code><br>= ${num(L.cC)} + ${num(L.cR)} = <span class="res">${num(L.carbon)} kgCO₂e</span></div>
      <div class="hint">${esc(S.note)}</div>`;
  } else {
    const af = d.building?.AFu;
    body += `
      <div><div class="big-num">${num(S.above)}<small>kgCO₂e</small></div><div class="muted small">整棟地上主結構 CFs${af ? `｜${num(S.above / af, 1)} kgCO₂e/m²` : ""}</div></div>
      <div class="hint">${esc(S.note || "這個樓層沒有資料。")}</div>`;
  }

  $("#comp-panel").innerHTML = `
    <div class="panel-head"><h2>${CAT[cat].label}｜${esc(level)}</h2><button class="icon-btn" id="panel-close" aria-label="關閉">✕</button></div>
    <div class="meta"><span class="badge blue">${ZONE[zone]}</span>${methodBadge}<span class="muted small">${esc(state.source)}</span></div>
    ${body}
    <a class="btn primary block" href="dashboard.html" data-open-dashboard>在計算結果中查看 →</a>
    <p class="muted small">示意模型依樓層數產生，不是實際幾何；數字來自${state.source === "示範資料" ? "首頁示範資料" : "載入的 JSON"}。</p>`;
  $("#comp-panel").hidden = false;
  $("#panel-close").onclick = closePanel;
  $$("[data-pick]", $("#comp-panel")).forEach((r) => r.addEventListener("click", () => select(r.dataset.pick, level, null)));
}

function levelTable(level, cur) {
  const rows = Object.keys(CAT).map((k) => {
    const g = state.summary.groups.get(k + "|" + level);
    return { k, c: g ? g.cC + g.cR : 0 };
  }).filter((r) => r.c > 0);
  if (!rows.length) return "";
  const sum = rows.reduce((a, r) => a + r.c, 0);
  return `
    <details class="more"${cur ? "" : " open"}><summary>${esc(level)} 各類別</summary><div class="inner">
      <table class="mini-table">
        <tr><th>類別</th><th class="num">kgCO₂e</th><th class="num">占本層</th></tr>
        ${rows.map((r) => `<tr data-pick="${r.k}" style="cursor:pointer${r.k === cur ? ";color:#8bb8ff" : ""}"><td>${CAT[r.k].label}</td><td class="num">${num(r.c)}</td><td class="num">${pct(r.c / sum)}</td></tr>`).join("")}
        <tr><td><b>合計</b></td><td class="num"><b>${num(sum)}</b></td><td></td></tr>
      </table></div></details>`;
}

function renderStats() {
  const S = state.summary, af = state.data.building?.AFu;
  $("#stats").innerHTML = `
    <div><small>Above-ground CFs</small><b>${tons(S.above)}<em>tCO₂e</em></b></div>
    <div><small>Basement CFs′</small><b>${tons(S.below)}<em>tCO₂e</em></b></div>
    <div><small>Per Floor Area</small><b>${af ? num(S.above / af, 1) : "—"}<em>kg/m²</em></b></div>`;
}
function renderSource() {
  const demo = state.source === "示範資料";
  const saved = demo && loadSaved(), newer = !demo && newerSaved(), time = !demo && resultTime(state.raw).slice(0, 16);
  $("#source-pill").innerHTML = `<i class="dot ${demo || newer ? "a" : "g"}"></i>`
    + (demo ? "Demo data · load JSON to use your model" : esc(state.source) + (time ? " · calculated " + esc(time) : ""))
    + (saved ? `<button type="button" data-source="saved">Back to my result</button>` : "")
    + (newer ? `<button type="button" data-source="saved">Show latest (${esc(resultTime(newer.obj).slice(0, 16))})</button>` : "")
    + (demo ? "" : `<button type="button" data-source="demo">Demo</button>`);
}
$("#source-pill").addEventListener("click", (e) => {
  const b = e.target.closest("[data-source]");
  if (!b) return;
  if (b.dataset.source === "demo") useData(demoData(), "示範資料");
  else showSaved();
});

// ================================================================ 載入與分享

function normalize(o) {
  if (!o || typeof o !== "object" || Array.isArray(o)) return { error: "The file is not a valid JSON object." };
  if (o.format === "lebr-codes") return { error: "This is a code mapping file, not a result. Use “Export result JSON…” in the Revit result window." };
  if (o.format === "lebr-structure-result") return { data: o };
  if (Array.isArray(o.levels) && o.result && "cfs" in o.result) {
    return { data: { method: "bim_quantity", model: { name: o.model, exported_at: String(o.calculated_at || "").replace("T", " ") }, building: {}, quantities: [], factors: [], levels: o.levels, result: o.result }, dashboard: false };
  }
  if (isLegacy(o)) return { data: fromLegacy(o) };
  return { error: "Unknown format: expected lebr-structure-result, a plugin result JSON or an estimate record." };
}
// 最後一次載入的結果存在這台電腦的瀏覽器（與 dashboard 共用），回首頁時不會變回示範資料
const SAVED_KEY = "lebr:last-result";
// 計算時間（外掛的 calculated_at 或 lebr-structure-result 的 model.exported_at），用來判斷哪個結果比較新
const resultTime = (o) => String(o?.calculated_at ?? o?.model?.exported_at ?? "").replace("T", " ").slice(0, 19);
function loadSaved() {
  try { const s = JSON.parse(localStorage.getItem(SAVED_KEY) || "null"); return s && s.obj ? s : null; } catch (e) { return null; }
}
function newerSaved() {
  const s = loadSaved();
  return s && state.raw && resultTime(s.obj) > resultTime(state.raw) ? s : null;
}
// fromLink：連結帶進來的資料比這台電腦存的舊（例如舊分頁重新整理）就不覆蓋
function saveResult(obj, label, fromLink) {
  const s = fromLink && loadSaved();
  if (s && resultTime(s.obj) > resultTime(obj)) return;
  try { localStorage.setItem(SAVED_KEY, JSON.stringify({ label, obj })); } catch (e) { /* 私密視窗或容量不足：不影響顯示 */ }
}
function showSaved() {
  const s = loadSaved();
  const r = s && normalize(s.obj);
  if (!r || r.error) return false;
  useData(r.data, s.label || "上次的結果", r.dashboard === false ? null : s.obj);
  return true;
}

async function loadFile(file) {
  if (!file) return;
  try {
    const obj = JSON.parse(await file.text());
    const r = normalize(obj);
    if (r.error) return toast(r.error, 5000);
    useData(r.data, file.name, r.dashboard === false ? null : obj);
    saveResult(obj, file.name);
    toast("Loaded " + file.name);
  } catch (e) {
    toast("Cannot read the file: not valid JSON.", 4000);
  }
}
$("#file").addEventListener("change", (e) => loadFile(e.target.files[0]));
addEventListener("dragover", (e) => e.preventDefault());
addEventListener("drop", (e) => { e.preventDefault(); loadFile(e.dataTransfer.files[0]); });

const b64u = (buf) => {
  let s = ""; const a = new Uint8Array(buf);
  for (let i = 0; i < a.length; i += 0x8000) s += String.fromCharCode(...a.subarray(i, i + 0x8000));
  return btoa(s).replace(/\+/g, "-").replace(/\//g, "_").replace(/=+$/, "");
};
async function encodeData(obj) {
  const stream = new Blob([JSON.stringify(obj)]).stream().pipeThrough(new CompressionStream("gzip"));
  return b64u(await new Response(stream).arrayBuffer());
}
async function decodeData(s) {
  const bytes = Uint8Array.from(atob(s.replace(/-/g, "+").replace(/_/g, "/")), (c) => c.charCodeAt(0));
  const stream = new Blob([bytes]).stream().pipeThrough(new DecompressionStream("gzip"));
  return JSON.parse(await new Response(stream).text());
}
// 進入計算結果：把目前資料帶到 Dashboard（估算紀錄 Dashboard 不支援，只開頁面）
document.addEventListener("click", async (e) => {
  const a = e.target.closest("[data-open-dashboard]");
  if (!a || !state.raw || typeof CompressionStream === "undefined") return;
  e.preventDefault();
  try { location.href = "dashboard.html#d=" + (await encodeData(state.raw)); }
  catch (err) { location.href = "dashboard.html"; }
});

// ================================================================ 上方條列（點了才看詳細說明）

// 滑動膠囊（仿 iOS 分段控制）：游標停在哪一項就彈性滑過去，離開列表回到目前這一項
const navPill = document.createElement("span");
navPill.className = "nav-pill";
$("#topnav").prepend(navPill);
function moveNavPill(target) {
  const a = target || $("#topnav a.active") || $("#topnav a");
  navPill.style.width = a.offsetWidth + "px";
  navPill.style.transform = `translateX(${a.offsetLeft}px)`;
}
$("#topnav").addEventListener("pointerover", (e) => { const a = e.target.closest("a"); if (a) moveNavPill(a); });
$("#topnav").addEventListener("pointerleave", () => moveNavPill());
addEventListener("resize", () => moveNavPill());
moveNavPill();
document.fonts?.ready.then(() => moveNavPill());

const SHEETS = { intro: "LEBR 簡介", problem: "設計階段算碳排的三個痛點", features: "核心功能", flow: "運作流程", team: "團隊成員" };
function openSheet(id) {
  if (!SHEETS[id]) return closeSheet(true);
  $("#sheet-title").textContent = SHEETS[id];
  const ids = Object.keys(SHEETS), i = ids.indexOf(id);
  $("#sheet-prev").dataset.to = ids[(i + ids.length - 1) % ids.length];
  $("#sheet-next").dataset.to = ids[(i + 1) % ids.length];
  $("#sheet-prev").title = SHEETS[$("#sheet-prev").dataset.to];
  $("#sheet-next").title = SHEETS[$("#sheet-next").dataset.to];
  $(".sheet").scrollTop = 0;
  $("#sheet-body").innerHTML = "";
  $("#sheet-body").appendChild($("#tpl-" + id).content.cloneNode(true));
  $("#sheet-bg").classList.add("open");
  $$("#topnav a").forEach((a) => a.classList.toggle("active", a.getAttribute("href") === "#" + id));
  moveNavPill();
}
function closeSheet(silent) {
  $("#sheet-bg").classList.remove("open");
  $$("#topnav a").forEach((a) => a.classList.toggle("active", a.getAttribute("href") === "#home"));
  moveNavPill();
  if (!silent && location.hash) history.replaceState(null, "", location.pathname + location.search);
}
addEventListener("hashchange", () => { if (!location.hash.startsWith("#d=")) openSheet(location.hash.slice(1)); });
$("#sheet-close").addEventListener("click", () => closeSheet());
// 左右箭頭（或鍵盤 ← →）直接換到上一頁、下一頁，不用關掉再點上方列表
const stepSheet = (btn) => { location.hash = btn.dataset.to; };
$("#sheet-prev").addEventListener("click", (e) => { e.stopPropagation(); stepSheet(e.currentTarget); });
$("#sheet-next").addEventListener("click", (e) => { e.stopPropagation(); stepSheet(e.currentTarget); });
$("#sheet-bg").addEventListener("click", (e) => { if (e.target.id === "sheet-bg") closeSheet(); });
addEventListener("keydown", (e) => {
  if ($("#sheet-bg").classList.contains("open") && (e.key === "ArrowLeft" || e.key === "ArrowRight")) return stepSheet($(e.key === "ArrowLeft" ? "#sheet-prev" : "#sheet-next"));
  if (e.key !== "Escape") return;
  if ($("#sheet-bg").classList.contains("open")) closeSheet(); else if (state.sel) closePanel();
});

// ================================================================ 訪客名字（登入視窗在 visitor.js）
// 右上按鈕：沒輸入過名字顯示 Log in，輸入後顯示名字；第一次來首頁自動彈出
function renderVisitor() {
  const n = visitor.name();
  $("#btn-login").innerHTML = n ? `<span class="who-dot">${esc(n.slice(0, 1))}</span>${esc(n)}` : "Log in";
}
$("#btn-login").addEventListener("click", () => visitor.open());
addEventListener("visitorchange", renderVisitor);
renderVisitor();
if (!visitor.name()) setTimeout(() => { if (!visitor.name() && !$("#sheet-bg").classList.contains("open")) visitor.open(); }, 1200);

// ================================================================ 啟動
// 順序：網址 #d=（Revit「在圖台開啟」或分享連結）→ 這台電腦上次的結果 → 示範資料
async function start() {
  const m = location.hash.match(/^#d=([\w-]+)$/);
  if (m && typeof DecompressionStream !== "undefined") {
    try {
      const obj = await decodeData(m[1]);
      const r = normalize(obj);
      if (!r.error) {
        useData(r.data, r.data.model?.name || "Revit 連結", r.dashboard === false ? null : obj);
        saveResult(obj, r.data.model?.name || "Revit 連結", true);
        history.replaceState(null, "", location.pathname + location.search);
        if (newerSaved()) toast("This link is not the latest result; use “Show latest” at the bottom right", 6000);
        return;
      }
      toast(r.error, 5000);
    } catch (e) {
      toast("The link data could not be read; showing the last result or demo data.", 5000);
    }
  }
  if (showSaved()) return;
  useData(demoData(), "示範資料");
}
// 另一個分頁（Revit 新開的首頁或 Dashboard）載入了較新的結果：這頁跟著換，避免進入計算結果時帶到舊的
addEventListener("storage", (e) => {
  if (e.key !== SAVED_KEY) return;
  if (state.source === "示範資料") return renderSource();
  if (newerSaved() && showSaved()) toast("Another tab loaded a newer result; switched to it", 4000);
});
start().finally(() => { if (location.hash && !location.hash.startsWith("#d=")) openSheet(location.hash.slice(1)); });
requestAnimationFrame(frame);
