// 依 mcp_commands 清單在 Revit 建窗並逐步回讀驗證;另含模型狀態、窗型檢查、還原與人工檢查紀錄。
import { withRevit, paramsOf, pick } from './revit_client.mjs';
import { getModel, conventions, readJson, safePath, writeOutput, stamp, listModels, OUTPUT_DIR } from './catalog.mjs';
import fs from 'node:fs';

// ---------- 步驟 1:Revit 連線與作用中模型 ----------
// 辨識作用中的是哪個模型:先比專案名稱;另存新檔後名稱會變,改用「只有該模型才有的牆 ID」辨識
async function identify(rv) {
  const name = (await rv.call('get_project_info')).data?.ProjectName ?? null;
  const models = listModels().map((m) => getModel(m.model_id));
  const byName = models.find((m) => m.revit_project_name === name);
  if (byName) return { name, model_id: byName.model_id, method: 'project_name' };
  const wallIds = (m) => m.building.walls.map((w) => w.wall_id);
  const hits = [];
  for (const m of models) {
    const others = new Set(models.filter((o) => o !== m).flatMap(wallIds));
    const unique = wallIds(m).filter((id) => !others.has(id));
    const foreign = [...others].filter((id) => !wallIds(m).includes(id));
    if (!unique.length) continue;
    let ok = true;
    for (const id of unique) if (!(await rv.call('get_wall_info', { wallId: Number(id) })).ok) { ok = false; break; }
    for (const id of foreign) if (ok && (await rv.call('get_wall_info', { wallId: Number(id) })).ok) ok = false;
    if (ok) hits.push(m.model_id);
  }
  return { name, model_id: hits.length === 1 ? hits[0] : null, method: hits.length === 1 ? 'wall_fingerprint' : null };
}

export async function revitStatus() {
  try {
    return await withRevit(async (rv) => {
      const id = await identify(rv);
      return { connected: true, active_project: id.name, matched_model_id: id.model_id, matched_by: id.method };
    });
  } catch (e) { return { connected: false, error: e.message }; }
}

async function assertActive(rv, modelId) {
  const m = getModel(modelId);
  const id = await identify(rv);
  if (id.model_id !== m.model_id) throw new Error(`Revit 作用中專案是「${id.name}」,${id.model_id ? `辨識為模型 ${id.model_id}` : '無法辨識為任何模型'},不是模型 ${m.model_id}。兩個模型的 ElementId 不通用,請先在 Revit 切換到正確的專案。`);
  return { ...m, active_project: id.name, matched_by: id.method };
}

// 模型比對:building_data.json 的牆與房間是否存在且幾何一致(唯讀)
export async function verifyModel(modelId) {
  return withRevit(async (rv) => {
    const m = await assertActive(rv, modelId);
    const near = (a, b, t = 1) => Math.abs(a - b) <= t;
    const walls = [];
    for (const w of m.building.walls) {
      const r = await rv.call('get_wall_info', { wallId: Number(w.wall_id) });
      if (!r.ok) { walls.push({ wall_id: w.wall_id, ok: false, error: r.error }); continue; }
      const d = r.data, s = w.location_line.start_xyz_m, e = w.location_line.end_xyz_m;
      const ok = near(d.Length, w.length_m * 1000) && near(d.Height, w.height_m * 1000) && near(d.StartX, s[0] * 1000) && near(d.StartY, s[1] * 1000) && near(d.EndX, e[0] * 1000) && near(d.EndY, e[1] * 1000);
      walls.push({ wall_id: w.wall_id, orientation: w.orientation.cardinal, is_exterior: w.is_exterior, ok, wall_type: d.WallType });
    }
    const columns = [];
    for (const c of m.building.columns) {
      const r = await rv.call('get_element_info', { elementId: c.ElementId });
      columns.push({ id: c.ElementId, ok: r.ok, type: r.data?.Name ?? null });
    }
    const rooms = [];
    for (const room of m.building.rooms) {
      const r = await rv.call('get_element_info', { elementId: Number(room.room_id) });
      rooms.push({ room_id: room.room_id, name: room.name, ok: r.ok });
    }
    const all = [...walls, ...columns, ...rooms];
    return { model_id: m.model_id, revit_project: m.revit_project_name, ok: all.every((x) => x.ok), walls, columns, rooms };
  });
}

// ---------- 步驟 2:窗型 ----------
// MCP 沒有載入 .rfa 的工具:這裡列出需要的窗型與目前已載入的窗型,缺的請在 Revit「插入 > 載入族群」載入。
export async function windowTypes(modelId) {
  const m = getModel(modelId);
  const required = m.families.families.flatMap((f) => (f.types || []).map((t) => `${f.family_name}: ${t.type_name || t.name}`));
  return withRevit(async (rv) => {
    await assertActive(rv, modelId);
    const fams = [...new Set(m.families.families.map((f) => f.family_name))];
    const loaded = new Set();
    for (const f of fams) {
      const r = await rv.call('list_family_symbols', { filter: f });
      for (const s of r.data?.Symbols || []) if (s.FamilyName === f) loaded.add(`${s.FamilyName}: ${s.Name}`);
    }
    return {
      model_id: m.model_id,
      required: required.map((t) => ({ family_and_type: t, loaded: loaded.has(t) })),
      missing: required.filter((t) => !loaded.has(t)),
      how_to_load: 'Revit「插入」>「載入族群」,從 Revit 公制族群庫 Windows 資料夾載入(MCP 無法代為載入)',
    };
  });
}

// ---------- 步驟 4:模型內既有的窗 ----------
// 本工具先前建窗(build_report 未還原、未被取代)的 ElementId → 報告
function previousBuilds(modelId) {
  const id = String(modelId).padStart(2, '0');
  if (!fs.existsSync(OUTPUT_DIR)) return [];
  return fs.readdirSync(OUTPUT_DIR).filter((f) => f.startsWith(`build_report_model${id}_`)).map((f) => {
    const file = `output/${f}`;
    const r = readJson(file);
    return { file, report: r };
  }).filter(({ report: r }) => !r.rolled_back_at && !r.superseded_at);
}

// 窗沿宿主牆的佔用範圍(mm),用範圍框投影到牆的 s 軸
function sRange(bb, wall) {
  const s0 = wall.location_line.start_xyz_m.map((v) => v * 1000), u = wall.location_line.s_axis_unit_xyz;
  const ss = [[bb.MinX, bb.MinY], [bb.MaxX, bb.MinY], [bb.MinX, bb.MaxY], [bb.MaxX, bb.MaxY]].map(([x, y]) => (x - s0[0]) * u[0] + (y - s0[1]) * u[1]);
  return [Math.min(...ss), Math.max(...ss)];
}

async function scanWindows(rv, m) {
  const conv = conventions();
  const q = await rv.call('query_elements', { category: 'Windows' });
  if (!q.ok) throw new Error(`無法列出模型內的窗:${q.error}`);
  const ids = (q.data?.Elements || []).map((e) => e.ElementId);
  const owner = new Map();
  for (const { file, report } of previousBuilds(m.model_id)) for (const id of report.created_element_ids) owner.set(id, { report_file: file, proposal_id: report.proposal.proposal_id });
  const walls = new Map(m.building.walls.map((w) => [w.wall_id, w]));
  const out = [];
  for (const id of ids) {
    const p = paramsOf((await rv.call('get_element_info', { elementId: id })).data);
    const host = String(pick(p, conv.host_parameter_names));
    const bb = (await rv.call('get_element_geometry', { elementId: id, geometryType: 'boundingbox' })).data?.BoundingBox;
    const wall = walls.get(host);
    out.push({ element_id: id, family_and_type: pick(p, conv.family_and_type_parameter_names), host_wall_id: host, s_range_mm: bb?.HasBoundingBox && wall ? sRange(bb, wall).map((v) => +v.toFixed(0)) : null, created_by: owner.get(id) ?? null });
  }
  return out;
}

// 給確認畫面:哪些窗會因「取代前一組方案」被刪除、哪些不是本工具建立的
export async function existingWindows(modelId) {
  return withRevit(async (rv) => {
    const m = await assertActive(rv, modelId);
    const all = await scanWindows(rv, m);
    const prev = all.filter((w) => w.created_by);
    return {
      model_id: m.model_id,
      total: all.length,
      previous_proposal_windows: prev,
      previous_proposals: [...new Set(prev.map((w) => w.created_by.proposal_id))],
      other_windows: all.filter((w) => !w.created_by),
      note: 'previous_proposal_windows 會在建立新方案前自動刪除;other_windows 不是本工具建立的,只有在 delete_window_ids 指定時才刪除',
    };
  });
}

// ---------- 步驟 4:建窗 ----------
function measure(bb, g) {
  const [sx, sy] = g.start_mm, u = g.s_axis_unit, n = g.outward_normal;
  const cx = (bb.MinX + bb.MaxX) / 2, cy = (bb.MinY + bb.MaxY) / 2;
  const corners = [[bb.MinX, bb.MinY], [bb.MaxX, bb.MinY], [bb.MinX, bb.MaxY], [bb.MaxX, bb.MaxY]].map(([x, y]) => (x - sx) * n[0] + (y - sy) * n[1]);
  return { s_center_mm: +((cx - sx) * u[0] + (cy - sy) * u[1]).toFixed(1), outward_extent_mm: +Math.max(...corners).toFixed(1), inward_extent_mm: +(-Math.min(...corners)).toFixed(1), z_min_mm: bb.MinZ, z_max_mm: bb.MaxZ };
}
const bboxOf = (b) => ({ MinX: b.MinX, MinY: b.MinY, MinZ: b.MinZ, MaxX: b.MaxX, MaxY: b.MaxY, MaxZ: b.MaxZ });
const overlap2d = (a, b) => a.MinX < b.MaxX && b.MinX < a.MaxX && a.MinY < b.MaxY && b.MinY < a.MaxY;

// 窗建立後被 Revit 移除(通常是 Revit 跳出錯誤對話框,例如與既有窗重疊,使用者選了「刪除實體」)
function gone(rec, at) {
  rec.steps.push({ step: at, ok: false, error: 'element no longer exists' });
  rec.issues.push(`窗 ${rec.element_id} 建立後已被 Revit 移除(Revit 可能跳出錯誤對話框並刪除了實體,常見原因是與既有窗重疊或無法切割牆)`);
  rec.removed_by_revit = rec.element_id;
  rec.element_id = null;
  return rec;
}

async function runWindow(rv, win, columns, done, conv) {
  const rec = { window_no: win.window_no, wall_id: win.wall_id, orientation: win.orientation, expected: win.expected, element_id: null, status: 'fail', steps: [], issues: [] };
  const params = (s) => JSON.parse(JSON.stringify(s.params).replaceAll('"$element"', String(rec.element_id)));
  const step = (name) => win.steps.find((s) => s.step === name);
  const log = (name, r, extra = {}) => rec.steps.push({ step: name, ok: r.ok, error: r.error ?? null, ...extra });
  const readBox = async () => (await rv.call('get_element_geometry', { elementId: rec.element_id, geometryType: 'boundingbox' })).data?.BoundingBox;

  // create
  const c = step('create');
  const cr = await rv.call(c.tool, c.params);
  log('create', cr);
  if (!cr.ok) { rec.issues.push(`建窗失敗:${cr.error}`); return rec; }
  rec.element_id = cr.data.ElementId;

  // verify type & host(MCP 名稱對不上時會退回第一個窗型)
  const vi = await rv.call('get_element_info', params(step('verify_type_and_host')));
  if (!vi.ok) return gone(rec, 'verify_type_and_host');
  const p = paramsOf(vi.data);
  rec.actual = { family_and_type: pick(p, conv.family_and_type_parameter_names), host_wall_id: String(pick(p, conv.host_parameter_names)) };
  const typeOk = rec.actual.family_and_type === win.expected.family_and_type && rec.actual.host_wall_id === String(win.expected.host_wall_id);
  log('verify_type_and_host', { ok: vi.ok && typeOk, error: vi.error }, rec.actual);
  if (!typeOk) {
    const del = await rv.call('delete_element', { elementId: rec.element_id });
    rec.issues.push(`類型或宿主牆不符(${rec.actual.family_and_type} / ${rec.actual.host_wall_id}),${del.ok ? '已刪除' : '刪除失敗,請手動處理'}`);
    if (del.ok) rec.element_id = null;
    return rec;
  }

  // sill
  const sr = await rv.call('modify_element_parameter', params(step('set_sill')));
  log('set_sill', sr);
  if (!sr.ok) rec.issues.push(`窗台修改失敗:${sr.error}`);

  // facing
  let bb = await readBox();
  let mm = bb?.HasBoundingBox ? measure(bb, win.wall_geometry) : null;
  if (mm && mm.outward_extent_mm > mm.inward_extent_mm) {
    const fr = await rv.call('flip_element', params(step('face_interior')));
    log('face_interior', fr, { flipped: fr.ok });
    bb = await readBox();
    mm = bb?.HasBoundingBox ? measure(bb, win.wall_geometry) : null;
  } else log('face_interior', { ok: !!mm }, { flipped: false });

  // verify geometry
  if (!mm) {
    const still = await rv.call('get_element_info', { elementId: rec.element_id });
    if (!still.ok) return gone(rec, 'verify_geometry');
    rec.issues.push('讀不到範圍框'); return rec;
  }
  rec.bbox_mm = bboxOf(bb);
  rec.measured = { ...mm, facing: mm.inward_extent_mm >= mm.outward_extent_mm ? 'interior' : 'exterior' };
  const e = win.expected;
  if (Math.abs(mm.s_center_mm - e.s_center_mm) > e.tolerance_mm) rec.issues.push(`沿牆位置偏差 ${(mm.s_center_mm - e.s_center_mm).toFixed(1)} mm`);
  if (!(mm.z_max_mm >= e.head_mm - e.tolerance_mm && mm.z_max_mm <= e.head_mm + 150)) rec.issues.push(`窗頂 ${mm.z_max_mm} mm 與預期 ${e.head_mm} mm(+窗框)不符`);
  if (rec.measured.facing !== 'interior') rec.issues.push('窗扇仍朝室外');
  const hitCols = columns.filter((col) => overlap2d(rec.bbox_mm, col.bbox_mm)).map((col) => col.id);
  const hitWins = done.filter((d) => d.bbox_mm && d.wall_id === win.wall_id && overlap2d(rec.bbox_mm, d.bbox_mm)).map((d) => d.element_id);
  if (hitCols.length) rec.issues.push(`範圍框與柱重疊:${hitCols.join(', ')}`);
  if (hitWins.length) rec.issues.push(`範圍框與同牆其他窗重疊:${hitWins.join(', ')}`);
  log('verify_geometry', { ok: !rec.issues.length });
  rec.status = rec.issues.length ? 'warning' : 'pass';
  return rec;
}

// options.replace_previous(預設 true):先刪除本工具先前在同模型建的窗(前一組方案)
// options.delete_window_ids:另外指定要刪除的窗(例如不是本工具建立、但會擋到新方案的窗)
export async function build(commandsFile, confirm, options = {}) {
  const cmds = readJson(commandsFile);
  if (cmds.schema !== 'mcp_commands/1.0') throw new Error(`不支援的指令格式:${cmds.schema}`);
  if (confirm !== cmds.proposal.proposal_id) throw new Error(`未確認:${cmds.confirmation_rule}`);
  const replacePrevious = options.replace_previous !== false;
  const extraIds = (options.delete_window_ids || []).map(Number);
  const conv = conventions();
  const reportName = `build_report_model${cmds.model_id}_${cmds.proposal.proposal_id}_${stamp()}.json`;
  return withRevit(async (rv) => {
    // preflight
    const active = await identify(rv);
    const pre = [];
    for (const c of cmds.preflight) {
      if (c.check === 'active_project') { pre.push({ ...c, ok: active.model_id === cmds.model_id, got: active.name, matched_by: active.method }); continue; }
      const r = await rv.call(c.tool, c.params || {});
      let ok = r.ok;
      if (c.check === 'window_type_loaded') ok = (r.data?.Symbols || []).some((s) => `${s.FamilyName}: ${s.Name}` === c.expect.family_and_type);
      pre.push({ ...c, ok, error: r.error ?? null, got: c.check === 'active_project' ? r.data?.ProjectName : undefined });
    }
    const bad = pre.filter((x) => !x.ok);
    if (bad.length) {
      const msg = bad.map((b) => b.check === 'active_project' ? `作用中專案是「${b.got}」,不是模型 ${cmds.model_id}` : b.check === 'window_type_loaded' ? `未載入窗型 ${b.expect.family_and_type}` : `宿主牆 ${b.params.wallId} 不存在`).join(';');
      throw new Error(`建窗前檢查未通過:${msg}`);
    }

    // 清理:先算好要刪哪些、會不會還有擋路的窗,全部確定後才動手(避免刪到一半才中止)
    const m = getModel(cmds.model_id);
    const existing = await scanWindows(rv, m);
    const toDelete = existing.filter((w) => (replacePrevious && w.created_by) || extraIds.includes(w.element_id));
    const remaining = existing.filter((w) => !toDelete.includes(w));
    const conflicts = remaining.filter((w) => w.s_range_mm && cmds.windows.some((n) => n.wall_id === w.host_wall_id
      && w.s_range_mm[0] < n.expected.s_center_mm + n.expected.width_mm / 2 + 100 && n.expected.s_center_mm - n.expected.width_mm / 2 - 100 < w.s_range_mm[1]));
    if (conflicts.length) {
      const e = new Error(`模型內有 ${conflicts.length} 扇窗會和新方案重疊(Revit 會跳出錯誤對話框):${conflicts.map((c) => `${c.element_id}(牆 ${c.host_wall_id}${c.created_by ? `,${c.created_by.proposal_id}` : ',非本工具建立'})`).join('、')}。若要一併刪除,請在 delete_window_ids 指定這些 ID。`);
      e.detail = { conflicts };
      throw e;
    }
    const cleanup = { replace_previous: replacePrevious, deleted: [], failed: [] };
    for (const w of toDelete) {
      const r = await rv.call('delete_element', { elementId: w.element_id });
      (r.ok ? cleanup.deleted : cleanup.failed).push({ element_id: w.element_id, created_by: w.created_by, error: r.error ?? null });
    }
    if (cleanup.failed.length) throw new Error(`刪除舊窗失敗,未建立新窗:${cleanup.failed.map((f) => `${f.element_id} ${f.error}`).join(';')}`);
    // 同模型的舊報告一律標為已取代(包括窗已不在模型裡的,例如被 Revit 刪掉或手動刪掉)
    const oldReports = replacePrevious ? previousBuilds(m.model_id).map((b) => b.file) : [...new Set(cleanup.deleted.filter((d) => d.created_by).map((d) => d.created_by.report_file))];
    for (const f of oldReports) {
      const old = readJson(f);
      Object.assign(old, { superseded_at: new Date().toISOString(), superseded_by: `output/${reportName}` });
      fs.writeFileSync(safePath(f), JSON.stringify(old, null, 1));
    }

    const started = new Date().toISOString();
    const results = [];
    for (const w of cmds.windows) results.push(await runWindow(rv, w, cmds.columns_for_overlap_check, results, conv));

    const created = results.filter((r) => r.element_id).map((r) => r.element_id);
    const fails = results.filter((r) => r.status === 'fail');
    const report = {
      schema: 'build_report/1.0',
      model_id: cmds.model_id, revit_project_name: active.name, base_project_name: cmds.revit_project_name, model_matched_by: active.method,
      proposal: cmds.proposal, commands_file: commandsFile, source_proposals_file: cmds.source_proposals_file,
      confirmed_by_user: true, started_at: started, finished_at: new Date().toISOString(),
      cleanup_before_build: cleanup,
      summary: { requested: cmds.windows.length, created: created.length, pass: results.filter((r) => r.status === 'pass').length, warning: results.filter((r) => r.status === 'warning').length, fail: fails.length },
      created_element_ids: created,
      VC_006: {
        check_id: 'VC-006', item: '確認 Revit family、type、host wall、位置及碰撞狀態',
        status: fails.length ? 'fail' : 'requires_verification',
        result: { family_loaded: true, type_resolved: !fails.some((f) => f.actual), host_wall_resolved: !fails.some((f) => f.actual), facing: results.every((r) => !r.measured || r.measured.facing === 'interior') ? 'interior' : 'mixed', collision_status: 'unknown' },
        evidence: ['MCP 指令回傳', 'Revit ElementId', '範圍框平面重疊預檢(柱、同牆窗)'],
        note: '完整 3D 碰撞與牆體切割須人工在 Revit 檢查後,用 /api/review 寫回結果',
        reviewer: null,
      },
      failures_for_member3: fails.map((r) => ({ proposal_id: cmds.proposal.proposal_id, window_no: r.window_no, wall_id: r.wall_id, issues: r.issues })),
      windows: results,
    };
    const file = writeOutput(reportName, report);
    return { file, report };
  });
}

// ---------- 還原、人工檢查 ----------
export async function rollback(reportFile) {
  const report = readJson(reportFile);
  return withRevit(async (rv) => {
    await assertActive(rv, report.model_id);
    const out = [];
    if (report.superseded_at) throw new Error(`這次建窗已被新方案取代(${report.superseded_by}),窗已經刪除`);
    for (const id of report.created_element_ids) out.push({ id, ...(await rv.call('delete_element', { elementId: id })) });
    report.rolled_back_at = new Date().toISOString();
    report.rollback = out.map((o) => ({ id: o.id, ok: o.ok, error: o.error }));
    fs.writeFileSync(safePath(reportFile), JSON.stringify(report, null, 1));
    return { deleted: out.filter((o) => o.ok).length, failed: out.filter((o) => !o.ok).map((o) => ({ id: o.id, error: o.error })) };
  });
}

export function review(reportFile, { collision, wall_cut, reviewer }) {
  if (!['pass', 'fail'].includes(collision)) throw new Error('collision 只能是 pass 或 fail');
  const report = readJson(reportFile);
  const v = report.VC_006;
  v.result.collision_status = collision;
  v.result.wall_cut_status = wall_cut || 'unknown';
  v.reviewer = reviewer || null;
  v.reviewed_at = new Date().toISOString();
  v.evidence = [...new Set([...v.evidence, 'Revit 3D 視圖人工檢查(牆體切割)', 'Revit 干涉檢查'])];
  v.status = collision === 'pass' && wall_cut !== 'fail' && report.summary.fail === 0 && v.result.facing === 'interior' ? 'pass' : 'fail';
  fs.writeFileSync(safePath(reportFile), JSON.stringify(report, null, 1));
  return v;
}
