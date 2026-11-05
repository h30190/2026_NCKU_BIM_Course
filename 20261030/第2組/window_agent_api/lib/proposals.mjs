// 氣象 → 開窗方案。推薦邏輯在組員 3 的引擎(engine_member3/),這裡只負責餵入資料包內的檔案。
import path from 'node:path';
import { getModel, shared, safePath, readJson, writeOutput, stamp, listWeather } from './catalog.mjs';
import { generateProposals, customProposal, wallCapacities } from '../engine_member3/engine.mjs';
import { DEFAULTS } from '../engine_member3/data.mjs';
import { loadWindFromEpw, DEFAULT_COMFORT } from '../engine_member3/epw.mjs';

// options: { epw, months:[...], t_min_c, t_max_c, hour_from, hour_to, all_hours }
function comfortOf(o) {
  if (o.all_hours) return null;
  return { ...DEFAULT_COMFORT, ...Object.fromEntries(['t_min_c', 't_max_c', 'hour_from', 'hour_to'].filter((k) => o[k] != null).map((k) => [k, Number(o[k])])) };
}

export function weatherSummary(epwPath, options = {}) {
  const wind = loadWindFromEpw(safePath(epwPath), options.months || null, comfortOf(options));
  return {
    epw_file: epwPath,
    location: wind.location,
    period: wind.period,
    open_window_filter: wind.comfort ?? null,
    usable_hours: wind.hours,
    usable_fraction: +wind.usable_fraction.toFixed(3),
    monthly_usable_hours: wind.monthly_usable_hours,
    mean_speed_m_s: +wind.mean_speed_m_s.toFixed(2),
    prevailing_directions: wind.prevailing,
  };
}

const engineInputs = (m, epwFile, options) => {
  const s = shared();
  return { model: m.model_id, building: m.building, families: m.families, windowTypes: s.windowTypes, rules: s.rules, epwFile, wind: epwFile && loadWindFromEpw(epwFile, options.months || null, comfortOf(options)) };
};

// 產生方案並存成 output/proposals_modelXX_時間.json。輸出格式與交付包 window_proposals_modelXX.json 相同。
export function generate(modelId, options = {}) {
  const m = getModel(modelId);
  const epwFile = safePath(options.epw);
  const inputs = engineInputs(m, epwFile, options);
  const result = generateProposals(inputs, { max_proposals: options.max_proposals });
  const { cores, ...roomPublic } = result.room;
  const doc = {
    schema_version: '1.1',
    generated_for_model: `model_${m.model_id}`,
    status: 'proposed_pending_user_confirmation',
    scope: '只比較自然通風;不含採光、日射與風險分數',
    weather: { ...result.wind, epw_file: path.basename(epwFile) },
    weather_request: { epw: options.epw, months: options.months || null, open_window_filter: comfortOf(options) },
    units: { length: 'm', position_mm: 'mm (MCP locationX/Y)', area: 'm2', flow: 'm3/h' },
    coordinate_note: 'position_mm 為 Revit 內部座標換算成 mm;s_center_m 為沿牆 location line 自 start 起算的距離;方位為真北順時針',
    assumptions: {
      ...DEFAULTS,
      office_room: roomPublic,
      service_cores: cores.map((c) => ({ core_id: c.core_id, wall_ids: c.wall_ids, box_m: c.box, footprint_m2: +c.footprint_m2.toFixed(3) })),
    },
    proposals: result.proposals,
    rejected_summary: { count: result.rejected_count, sample: result.rejected_sample },
  };
  const file = writeOutput(`proposals_model${m.model_id}_${stamp()}.json`, doc);
  return { file, doc };
}

// 給確認畫面用的精簡版(逐窗只留平面示意圖需要的座標)
export function summarize(proposalsFile) {
  const doc = readJson(proposalsFile);
  return {
    file: proposalsFile,
    model: doc.generated_for_model,
    status: doc.status,
    weather: { city: doc.weather?.location?.city, station: doc.weather?.location?.station, period: doc.weather?.period, filter: doc.weather?.comfort ?? null, prevailing: doc.weather?.prevailing, epw_file: doc.weather?.epw_file },
    office_area_m2: doc.assumptions?.office_room?.floor_area_m2,
    proposals: doc.proposals.map((p) => ({
      proposal_id: p.proposal_id, rank: p.rank, scheme: p.scheme, score: p.score, window_count: p.window_count, family_type: p.family_type,
      per_wall: p.per_wall, reason: p.reason, assumption_flag: p.assumption_flag, requires_user_confirmation: p.requires_user_confirmation,
      checks: Object.fromEntries(Object.entries(p.checks).map(([k, v]) => [k, v.status])),
      windows: p.windows.map((w) => ({ wall_id: w.wall_id, orientation: w.orientation, x_m: w.position_mm.x / 1000, y_m: w.position_mm.y / 1000, width_m: w.width_m, facing_core: w.facing_core, cross_flow: w.cross_flow })), // 平面示意圖用
      score_components: p.score_components,
      metrics: { legal_m2: p.checks['VC-003']?.result?.legal_effective_open_area_m2, required_m2: p.checks['VC-003']?.result?.required_m2, vp: p.checks['VC-004']?.result?.VP, q_m3_h: p.checks['VC-005']?.result?.Q_m3_h },
      selection: p.selection ?? null,
      manual: p.manual ?? null,
      warnings: p.warnings ?? [],
    })),
  };
}

// 手動調整用:各外牆、各窗型最多能放幾扇,以及方案目前的配置
export function manualOptions(modelId, proposalsFile, proposalId) {
  const m = getModel(modelId);
  const caps = wallCapacities(engineInputs(m, null, {}));
  const p = readJson(proposalsFile).proposals.find((x) => x.proposal_id === proposalId);
  if (!p) throw new Error(`${proposalsFile} 沒有方案 ${proposalId}`);
  const current = {};
  for (const w of p.windows) (current[w.wall_id] ||= { count: 0, type: w.mcp_windowType }).count++;
  return { ...caps, current };
}

// 方案檔記錄的氣象條件;舊的預設方案檔沒有 weather_request,改用資料夾裡唯一的 EPW 和檔內的篩選條件
function weatherRequestOf(doc) {
  if (doc.weather_request?.epw) return { epw: doc.weather_request.epw, months: doc.weather_request.months, ...(doc.weather_request.open_window_filter ?? { all_hours: true }) };
  const epws = listWeather();
  if (epws.length !== 1) throw new Error('這個方案檔沒有記錄氣象檔,而 data/weather/ 有多個 EPW,請先用「產生方案」重新產生');
  const months = /^months (.+)$/.exec(doc.weather?.period ?? '')?.[1].split(',').map(Number) ?? null;
  return { epw: epws[0].file, months, ...(doc.weather?.comfort ?? { all_hours: true }) };
}

// 手動調整窗數/窗型:以原方案檔為底,加入新方案(編號 P1-M1、P1-M2…)後另存新檔
export function custom(modelId, { proposals_file, proposal_id, per_wall }) {
  const m = getModel(modelId);
  const doc = readJson(proposals_file);
  if (doc.generated_for_model !== `model_${m.model_id}`) throw new Error(`${proposals_file} 不是模型 ${m.model_id} 的方案`);
  const base = proposal_id.replace(/-M\d+$/, '');
  const req = weatherRequestOf(doc);
  const p = customProposal(engineInputs(m, safePath(req.epw), req), per_wall);
  const n = doc.proposals.filter((x) => x.proposal_id.startsWith(`${base}-M`)).length + 1;
  p.proposal_id = `${base}-M${n}`;
  p.manual = { based_on: proposal_id, created_at: new Date().toISOString() };
  const out = { ...doc, weather_request: doc.weather_request ?? { epw: req.epw, months: req.months, open_window_filter: comfortOf(req) }, proposals: [...doc.proposals, p] };
  const file = writeOutput(`proposals_model${m.model_id}_${stamp()}.json`, out);
  return { ...summarize(file), new_proposal_id: p.proposal_id };
}

// 刪除手動方案:只能刪 P1-M1 這類自選方案,自動方案不能刪。和 custom 一樣另存新檔,舊檔(可能已被建窗報告引用)不動
export function removeCustom(modelId, { proposals_file, proposal_id }) {
  const m = getModel(modelId);
  const doc = readJson(proposals_file);
  if (doc.generated_for_model !== `model_${m.model_id}`) throw new Error(`${proposals_file} 不是模型 ${m.model_id} 的方案`);
  const p = doc.proposals.find((x) => x.proposal_id === proposal_id);
  if (!p) throw new Error(`${proposals_file} 沒有方案 ${proposal_id}`);
  if (!p.manual) throw new Error(`${proposal_id} 是自動產生的方案,只能刪除手動方案`);
  const file = writeOutput(`proposals_model${m.model_id}_${stamp()}.json`, { ...doc, proposals: doc.proposals.filter((x) => x !== p) });
  return { ...summarize(file), deleted_proposal_id: proposal_id };
}
