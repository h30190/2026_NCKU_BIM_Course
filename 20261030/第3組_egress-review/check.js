// commit 前執行：只檢查「會造成 bug 的地方」（對照 docs/整合規則.md 的編號）。其他寫法不管。
//   node check.js            全部檢查
//   node check.js B          另外提醒 B 改到了哪些不是自己主要負責的檔案（只提醒，不擋）
//   node check.js --relock   改完共用接口檔案後，更新指紋
// 【不能改】這個檔案
const fs = require("fs");
const path = require("path");
const vm = require("vm");
const crypto = require("crypto");
const { execSync } = require("child_process");

const ROOT = __dirname;
const rel = (...p) => path.join(ROOT, ...p);
const own = JSON.parse(fs.readFileSync(rel("ownership.json"), "utf8"));
const LOCK = rel("lock.json");
const CR = String.fromCharCode(13);
const sha = (f) => crypto.createHash("sha256").update(fs.readFileSync(rel(f), "utf8").split(CR).join("")).digest("hex");
const guarded = [...own.locked.files, ...own.shared.files];
const out = []; // [等級, 規則編號, 說明]  等級：ok / fail / warn
const ok = (n, s) => out.push(["ok", n, s]);
const fail = (n, s) => out.push(["fail", n, s]);
const warn = (n, s) => out.push(["warn", n, s]);
const git = (cmd) => execSync("git " + cmd, { cwd: ROOT, stdio: ["ignore", "pipe", "ignore"] }).toString();
const arg = process.argv[2];

if (arg === "--relock") {
  const lock = Object.fromEntries(guarded.filter((f) => fs.existsSync(rel(f))).map((f) => [f, sha(f)]));
  fs.writeFileSync(LOCK, JSON.stringify(lock, null, 2) + "\n", "utf8");
  console.log("已更新 lock.json。記得在群組說你改了哪個共用檔案。");
  process.exit(0);
}
const role = arg && arg.toUpperCase();

// 第 15 條、共用接口：法規原文與檢查工具不能改；接口檔案改了要 relock
const lock = fs.existsSync(LOCK) ? JSON.parse(fs.readFileSync(LOCK, "utf8")) : {};
let touched = false;
for (const f of guarded) {
  if (!lock[f]) continue;
  const changed = !fs.existsSync(rel(f)) || sha(f) !== lock[f];
  if (!changed) continue;
  touched = true;
  if (own.locked.files.includes(f)) fail(15, `${f} 不能改，請用 git checkout -- "${f}" 還原`);
  else warn("共用", `${f} 改過了。確認已在群組說過，再執行 node check.js --relock`);
}
if (!touched) ok(15, "法規原文、檢查工具、共用接口都沒變");

// 讀資料：第 4 條 BOM、第 6 條格式
const bomSeen = new Set();
function readJson(f) {
  const buf = fs.readFileSync(rel(f));
  if (buf[0] === 0xef && buf[1] === 0xbb && buf[2] === 0xbf) { if (!bomSeen.has(f) && bomSeen.add(f)) fail(4,`${f} 開頭有 BOM，請存成 UTF-8（無 BOM）`); return JSON.parse(buf.slice(3).toString("utf8")); }
  return JSON.parse(buf.toString("utf8"));
}
const jsonFiles = [];
(function walk(d) {
  for (const e of fs.readdirSync(d, { withFileTypes: true })) {
    if ([".git", "node_modules"].includes(e.name)) continue;
    const p = path.join(d, e.name);
    if (e.isDirectory()) walk(p); else if (e.name.endsWith(".json")) jsonFiles.push(path.relative(ROOT, p));
  }
})(ROOT);
let jsonOk = true;
for (const f of jsonFiles) { try { readJson(f); } catch (e) { jsonOk = false; fail(4, `${f} 讀不進來：${e.message}`); } }
if (jsonOk && !out.some((r) => r[1] === 4)) ok(4, `${jsonFiles.length} 個 JSON 都是 UTF-8 無 BOM、格式正確`);

// 第 1、3、5、6、8 條：檢查每一份模型快照（範例和 A 的真實資料）
const polyArea = (pts) => Math.abs(pts.reduce((s, p, i) => { const q = pts[(i + 1) % pts.length]; return s + p[0] * q[1] - q[0] * p[1]; }, 0)) / 2;
const isNum = (v) => typeof v === "number" && Number.isFinite(v);
const isPt = (p) => Array.isArray(p) && p.length === 2 && isNum(p[0]) && isNum(p[1]);
for (const f of jsonFiles.filter((f) => /模型快照|real-/.test(f))) {
  let snap; try { snap = readJson(f); } catch { continue; }
  const errs = [], add = (n, m) => errs.push([n, m]);
  for (const k of ["project", "rooms", "doors", "stairs", "corridors", "warnings"]) if (!Array.isArray(snap[k]) && k !== "project") add(6, `缺少 ${k} 陣列`);
  const ids = [];
  for (const r of snap.rooms || []) {
    ids.push(r.element_id);
    if (!Array.isArray(r.door_ids)) add(8, `${r.name} 的 door_ids 不是陣列`);
    else r.door_ids.forEach((d) => typeof d !== "string" && add(3, `${r.name} 的 door_ids 有不是文字的編號`));
    if (!Array.isArray(r.boundary) || !r.boundary.every(isPt)) add(6, `${r.name} 的 boundary 格式不對`);
    else if (r.boundary.length > 2 && isNum(r.area_m2)) {
      const ratio = r.area_m2 / polyArea(r.boundary);
      if (ratio > 5 || ratio < 0.2) add(1, `${r.name} 的 area_m2 (${r.area_m2}) 跟外框面積差 ${ratio.toFixed(1)} 倍，可能沒從英尺換算成公尺`);
    }
    if (r.area_m2 === 0) add(5, `${r.name} 的 area_m2 是 0，不知道請填 null`);
  }
  for (const d of snap.doors || []) { ids.push(d.element_id); if (d.clear_width_m === 0) add(5, `門 ${d.element_id} 淨寬是 0，不知道請填 null`); if (d.position && !isPt(d.position)) add(6, `門 ${d.element_id} 的 position 格式不對`); }
  for (const s of snap.stairs || []) { ids.push(s.element_id); if (!isPt(s.entry_point)) add(6, `${s.name} 的 entry_point 格式不對`); }
  for (const c of snap.corridors || []) {
    ids.push(c.element_id);
    if (c.clear_width_m === 0) add(5, `${c.name} 淨寬是 0，不知道請填 null`);
    if (isNum(c.clear_width_m) && c.clear_width_m > 20) add(1, `${c.name} 淨寬 ${c.clear_width_m}，看起來不是公尺`);
  }
  ids.forEach((id) => typeof id !== "string" && add(3, `element_id ${id} 不是文字，請改成 "${id}"`));
  errs.length ? errs.forEach(([n, m]) => fail(n, `${f}：${m}`)) : ok("1/3/5/8", `${f}：單位、編號、空值、門陣列都正確`);
}

// 第 10、11 條：engine 的寫法
const engineFiles = fs.readdirSync(rel("engine")).filter((f) => f.endsWith(".js") && !f.endsWith(".test.js"));
const engErrs = [];
for (const f of engineFiles) {
  const src = fs.readFileSync(rel("engine", f), "utf8");
  if (/^\s*(import|export)\s/m.test(src)) engErrs.push([10, `engine/${f} 用了 import／export，網頁裡跑不動`]);
  for (const m of src.matchAll(/require\(\s*["']([^"']+)["']\s*\)/g)) if (!m[1].startsWith("./")) engErrs.push([10, `engine/${f} 用了套件 ${m[1]}，網頁裡跑不動`]);
  if (/Math\.random|Date\.now|new Date\(/.test(src) && f !== "review.js") engErrs.push([11, `engine/${f} 用了時間或亂數，重算結果會不一樣`]);
  if (/anthropic|openai|gemini|fetch\(|XMLHttpRequest/i.test(src)) engErrs.push(["原則1", `engine/${f} 呼叫了 AI 或網路；計算和判定只能用程式（計畫書第 1 節）`]);
}
engErrs.length ? engErrs.forEach(([n, m]) => fail(n, m)) : ok("10/11", "engine 沒用 import、套件、時間亂數，也沒呼叫 AI");

// 第 9 條：在「網頁的環境」載入 engine，確認接口都還在
try {
  const sandbox = {}; vm.createContext(sandbox);
  for (const f of ["geometry.js", "rules.js", "review.js"]) vm.runInContext(fs.readFileSync(rel("engine", f), "utf8"), sandbox, { filename: f });
  const need = { EgressGeometry: ["measureRoom"], EgressRules: ["walkLimit", "judgeRoom", "judgeCorridor"], EgressEngine: ["review"] };
  const miss = Object.entries(need).flatMap(([o, fns]) => fns.filter((fn) => !sandbox[o] || typeof sandbox[o][fn] !== "function").map((fn) => `${o}.${fn}`));
  miss.length ? fail(9, "網頁裡找不到：" + miss.join("、")) : ok(9, "engine 在網頁環境載入正常，接口函式都在");
} catch (e) { fail(10, "engine 在網頁環境載入失敗：" + e.message); }

// 組網頁＋檢查輸出：第 6、12、13、14 條
let batch, pack;
try { execSync("node build.js", { cwd: ROOT, stdio: "pipe" }); ok("build", "node build.js 成功"); }
catch (e) { fail("build", "node build.js 失敗：" + String(e.stderr || e.message).split("\n").find((l) => /Error/.test(l))); }
try { batch = readJson("data/sample-3-檢討批次.json"); pack = readJson("regs/rules.json"); } catch {}
if (batch && pack) {
  const STATUS = ["符合", "不符合", "需人工確認", "模型資料不足"];
  const ruleIds = new Set(pack.rules.map((r) => r.id));
  const errs = [];
  for (const k of ["batch_id", "inputs", "rooms", "corridors", "fix_list"]) if (!(k in batch)) errs.push([6, `檢討批次缺少 ${k}`]);
  for (const r of batch.rooms || []) {
    for (const k of ["element_id", "status", "reasons", "facts", "legal", "hints"]) if (!(k in r)) errs.push([6, `${r.name} 缺少 ${k}`]);
    if (!STATUS.includes(r.status)) errs.push([13, `${r.name} 的狀態「${r.status}」不是四種之一`]);
    if (r.facts) for (const k of ["path", "distance_m", "stair_id", "candidates", "turns"]) if (!(k in r.facts)) errs.push([6, `${r.name} facts 缺少 ${k}`]);
    if (r.legal) r.legal.rules.forEach((id) => !ruleIds.has(id) && errs.push([14, `${r.name} 用了 rules.json 沒有的規則 ${id}`]));
    if (r.status === "符合" && r.reasons.length) errs.push(["原則4", `${r.name} 還有待確認項目，卻被判成符合`]);
    if (r.legal && r.facts && r.facts.distance_exact_m != null && r.legal.pass !== (r.facts.distance_exact_m <= r.legal.limit_m)) errs.push([12, `${r.name} 判定沒有用原始距離比較`]);
  }
  for (const c of batch.corridors || []) {
    if (!STATUS.includes(c.status)) errs.push([13, `${c.name} 的狀態「${c.status}」不是四種之一`]);
    if (c.rule && !ruleIds.has(c.rule)) errs.push([14, `${c.name} 用了 rules.json 沒有的規則 ${c.rule}`]);
  }
  const noSrc = pack.rules.filter((r) => !r.source || !fs.existsSync(rel(r.source)));
  if (noSrc.length) errs.push([15, "規則沒有出處或出處檔案不存在：" + noSrc.map((r) => r.id).join("、")]);
  errs.length ? errs.forEach(([n, m]) => fail(n, m)) : ok("6/12/13/14", "輸出格式、狀態文字、規則編號、出處都正確");

  // 計畫書：同樣輸入算兩次要一樣
  try {
    for (const f of ["geometry.js", "rules.js", "review.js"]) delete require.cache[require.resolve("./engine/" + f)];
    const eng = require("./engine/review.js");
    const snap = readJson("data/sample-1-模型快照.json"), set = readJson("data/sample-2-專案設定.json"), o = { created_at: "x" };
    JSON.stringify(eng.review(snap, set, pack, o)) === JSON.stringify(eng.review(snap, set, pack, o))
      ? ok(11, "同樣輸入算兩次結果一樣") : fail(11, "同樣輸入算兩次結果不一樣");
  } catch (e) { fail(9, "執行檢討失敗：" + e.message); }
}

// 第 17、18 條：Git
const rvt = [];
(function walk(d) { for (const e of fs.readdirSync(d, { withFileTypes: true })) { if ([".git", "node_modules"].includes(e.name)) continue; const p = path.join(d, e.name); e.isDirectory() ? walk(p) : /\.(rvt|rfa)$/i.test(e.name) && rvt.push(path.relative(ROOT, p)); } })(ROOT);
rvt.length ? fail(18, "資料夾裡有 Revit 檔：" + rvt.join("、")) : ok(18, "沒有 .rvt");
try {
  const tracked = git("ls-files").split("\n").map((f) => f.replace(git("rev-parse --show-prefix").trim(), ""));
  const bad = own.generated.files.filter((f) => tracked.includes(f));
  bad.length ? fail(17, "自動產生的檔案被 commit 了：" + bad.join("、") + "（用 git rm --cached 移除）") : ok(17, "自動產生的檔案沒有被 commit");
  if (role && own.roles[role]) {
    const base = git("merge-base HEAD origin/team").trim();
    const changed = (git(`diff --name-only ${base}`) + git("ls-files --others --exclude-standard")).split("\n").filter(Boolean);
    const others = changed.filter((f) => !own.roles[role].main.some((m) => f.includes(m)) && !own.generated.files.some((g) => f.endsWith(g)));
    if (others.length) warn("提醒", `${role} 改到了不是主要負責的檔案：${others.join("、")}。沒問題就好，合併前跟負責的人說一聲`);
  }
} catch { /* 沒有 git，略過 */ }

const tag = { ok: "通過", fail: "失敗", warn: "提醒" };
for (const [lv, n, s] of out) console.log(`${tag[lv]}  [${n}] ${s}`);
const failed = out.filter((r) => r[0] === "fail").length;
console.log(failed ? `\n有 ${failed} 項會造成 bug，修好再 commit。[ ] 裡的數字對應 docs/整合規則.md` : "\n沒有會造成 bug 的問題，可以 commit。");
process.exit(failed ? 1 : 0);
