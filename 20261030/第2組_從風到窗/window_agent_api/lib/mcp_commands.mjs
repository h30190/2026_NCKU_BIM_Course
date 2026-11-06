// 方案 → MCP 建窗指令清單(mcp_commands)。純資料轉換,不連 Revit。
// 執行器(executor.mjs)只依這份清單動作,所以這裡就是「Agent 方案如何變成 Revit 指令」的唯一定義。
import { getModel, conventions, readJson, writeOutput, stamp } from './catalog.mjs';

export const SCHEMA = 'mcp_commands/1.0';
const TOL_MM = 5;

export function buildCommands(proposalsFile, proposalId, { sillParam } = {}) {
  const doc = readJson(proposalsFile);
  const modelId = doc.generated_for_model.replace('model_', '');
  const m = getModel(modelId);
  const conv = conventions();
  const p = doc.proposals.find((x) => x.proposal_id === proposalId);
  if (!p) throw new Error(`${proposalsFile} 沒有方案 ${proposalId}`);
  const walls = new Map(m.building.walls.map((w) => [w.wall_id, w]));
  const sillName = sillParam || conv.sill_parameter_names[0];

  const windows = p.windows.map((w, i) => {
    const wall = walls.get(w.wall_id);
    if (!wall) throw new Error(`第 ${i + 1} 扇窗的宿主牆 ${w.wall_id} 不在 model_${modelId} 的 building_data.json`);
    if (!wall.is_exterior) throw new Error(`第 ${i + 1} 扇窗的宿主牆 ${w.wall_id} 不是外牆`);
    const sillMm = w.sill_height_m * 1000;
    return {
      window_no: i + 1,
      wall_id: w.wall_id,
      orientation: w.orientation,
      expected: {
        family_and_type: w.mcp_windowType,
        host_wall_id: w.wall_id,
        s_center_mm: +(w.s_center_m * 1000).toFixed(1),
        sill_mm: sillMm,
        head_mm: sillMm + w.height_m * 1000,
        width_mm: w.width_m * 1000,
        facing: 'interior',
        tolerance_mm: TOL_MM,
      },
      wall_geometry: {
        start_mm: wall.location_line.start_xyz_m.map((v) => +(v * 1000).toFixed(2)),
        s_axis_unit: wall.location_line.s_axis_unit_xyz,
        outward_normal: wall.orientation.outward_normal_xyz,
      },
      steps: [
        { step: 'create', tool: 'create_window', params: { wallId: Number(w.wall_id), locationX: w.position_mm.x, locationY: w.position_mm.y, windowType: w.mcp_windowType } },
        { step: 'verify_type_and_host', tool: 'get_element_info', params: { elementId: '$element' }, on_fail: 'delete_element' },
        { step: 'set_sill', tool: 'modify_element_parameter', params: { elementId: '$element', parameterName: sillName, value: String(sillMm / conv.mm_per_ft) }, note: `${sillMm} mm ÷ ${conv.mm_per_ft} = ft(Revit 內部單位)` },
        { step: 'face_interior', tool: 'flip_element', params: { elementId: '$element', flipType: 'facing' }, condition: 'bbox 外凸量 > 內凸量(窗扇朝室外)時才執行' },
        { step: 'verify_geometry', tool: 'get_element_geometry', params: { elementId: '$element', geometryType: 'boundingbox' } },
      ],
    };
  });

  return {
    schema: SCHEMA,
    generated_at: new Date().toISOString(),
    model_id: modelId,
    revit_project_name: m.revit_project_name,
    source_proposals_file: proposalsFile,
    proposal: { proposal_id: p.proposal_id, rank: p.rank, scheme: p.scheme, window_count: p.window_count, family_type: p.family_type, reason: p.reason },
    requires_user_confirmation: true,
    confirmation_rule: `執行時必須再輸入一次方案編號 ${p.proposal_id}`,
    preflight: [
      { check: 'active_project', tool: 'get_project_info', expect: { ProjectName: m.revit_project_name } },
      // 逐一檢查方案用到的每種窗型(手動方案可能混用多種,family_type 會是「A + B」)
      ...[...new Set(p.windows.map((w) => w.mcp_windowType))].map((t) => ({ check: 'window_type_loaded', tool: 'list_family_symbols', params: { filter: t.split(':')[0] }, expect: { family_and_type: t } })),
      ...[...new Set(p.windows.map((w) => w.wall_id))].map((id) => ({ check: 'host_wall_exists', tool: 'get_wall_info', params: { wallId: Number(id) } })),
    ],
    columns_for_overlap_check: m.building.columns.map((c) => ({ id: c.ElementId, bbox_mm: c.BoundingBox })),
    windows,
    failure_policy: '單扇失敗不重試,記錄後繼續下一扇;類型或宿主牆不符時刪除該窗',
  };
}

export function saveCommands(proposalsFile, proposalId, opts) {
  const cmds = buildCommands(proposalsFile, proposalId, opts);
  const file = writeOutput(`mcp_commands_model${cmds.model_id}_${proposalId}_${stamp()}.json`, cmds);
  return { file, commands: cmds };
}
