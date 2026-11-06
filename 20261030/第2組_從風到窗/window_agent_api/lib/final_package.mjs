// 步驟 5:彙整最終成果 → output/final_modelXX_PX_時間/
import fs from 'node:fs';
import path from 'node:path';
import { readJson, shared, OUTPUT_DIR, rel, stamp } from './catalog.mjs';

export function exportFinal(reportFile) {
  const report = readJson(reportFile);
  if (report.rolled_back_at) throw new Error('這次建窗已經還原,不能匯出');
  if (report.superseded_at) throw new Error(`這次建窗已被新方案取代(${report.superseded_by}),請匯出新的那一份`);
  const cmds = readJson(report.commands_file);
  const props = readJson(report.source_proposals_file);
  const prop = props.proposals.find((p) => p.proposal_id === report.proposal.proposal_id);

  // 規範檢核表:VC-001~005 取自方案,VC-006 取自建窗報告
  const checklist = shared().checklist;
  checklist.project = { model: `model_${report.model_id}`, revit_project: report.revit_project_name, proposal_id: prop.proposal_id, scheme: prop.scheme };
  for (const item of checklist.checklist) {
    if (item.check_id === 'VC-006') Object.assign(item, { status: report.VC_006.status, result: report.VC_006.result, evidence: report.VC_006.evidence, reviewer: report.VC_006.reviewer });
    else if (prop.checks[item.check_id]) Object.assign(item, { status: prop.checks[item.check_id].status, result: prop.checks[item.check_id].result ?? null });
  }

  const dir = path.join(OUTPUT_DIR, `final_model${report.model_id}_${prop.proposal_id}_${stamp()}`);
  fs.mkdirSync(dir, { recursive: true });
  const put = (name, data) => fs.writeFileSync(path.join(dir, name), typeof data === 'string' ? data : JSON.stringify(data, null, 1), 'utf8');

  put('1_selected_proposal.json', { generated_for_model: props.generated_for_model, weather: props.weather, assumptions: props.assumptions, proposal: prop });
  put('2_mcp_commands.json', cmds);
  put('3_build_report.json', report);
  put('4_ventilation_checklist.json', checklist);

  const w = props.weather || {};
  const reviewed = report.VC_006.result.collision_status !== 'unknown';
  const lines = [
    `# 開窗成果:model_${report.model_id} ${prop.proposal_id}`,
    '',
    `- Revit 專案:${report.revit_project_name}`,
    `- 氣象:${w.location?.city ?? ''}(${w.epw_file ?? ''}),${w.period ?? ''}${w.comfort ? `,僅統計 ${w.comfort.t_min_c}–${w.comfort.t_max_c}°C、${w.comfort.hour_from}–${w.comfort.hour_to} 時` : ''};常見風向 ${(w.prevailing || []).join('/')}`,
    `- 方案:${prop.scheme},${prop.window_count} 扇 ${prop.family_type}`,
    `- 推薦原因:${prop.reason}`,
    `- 建窗:${report.summary.created}/${report.summary.requested} 扇(pass ${report.summary.pass}、warning ${report.summary.warning}、fail ${report.summary.fail})`,
    `- VC-006:${report.VC_006.status}(碰撞 ${report.VC_006.result.collision_status},檢查人 ${report.VC_006.reviewer ?? '未填'})`,
    '',
    '| # | 方位 | 牆 ID | 窗 ID | 狀態 | 窗扇 | 問題 |',
    '|---|---|---|---|---|---|---|',
    ...report.windows.map((x) => `| ${x.window_no} | ${x.orientation} | ${x.wall_id} | ${x.element_id ?? '-'} | ${x.status} | ${x.measured?.facing ?? '-'} | ${x.issues.join(';') || ''} |`),
    '',
    '## 檢核',
    ...checklist.checklist.map((c) => `- ${c.check_id} ${c.item}:${c.status}`),
    '',
    '## 尚待處理',
    ...(reviewed ? [] : ['- 尚未記錄人工碰撞/牆體切割檢查(/api/review)']),
    '- MCP 無法存檔:請在 Revit 另存 rvt(建議檔名 ' + `model_${report.model_id}_${prop.proposal_id}_windows.rvt` + '),放進此資料夾',
    '- 法定有效通風面積、辦公區面積為 PoC 暫算,正式使用前須由建築師或門窗廠覆核(VC-008)',
  ];
  put('README.md', lines.join('\n'));
  return { dir: rel(dir), files: fs.readdirSync(dir), reviewed };
}
