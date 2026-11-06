// 平面示意圖用的幾何:牆中心線、柱、服務核(單位 m,Revit 內部座標,X 東、Y 北)。
import { getModel } from './catalog.mjs';
import { deriveOfficeRoom } from '../engine_member3/data.mjs';

export function planGeometry(modelId) {
  const m = getModel(modelId);
  const b = m.building;
  const room = deriveOfficeRoom(b);
  return {
    model_id: m.model_id,
    walls: b.walls.map((w) => ({
      wall_id: w.wall_id,
      is_exterior: !!w.is_exterior,
      orientation: w.orientation?.cardinal ?? null,
      thickness_m: w.thickness_m,
      start: w.location_line.start_xyz_m.slice(0, 2),
      end: w.location_line.end_xyz_m.slice(0, 2),
    })),
    columns: (b.columns || []).filter((c) => c.BoundingBox?.HasBoundingBox).map((c) => {
      const bb = c.BoundingBox;
      return { id: c.ElementId, minX: bb.MinX / 1000, minY: bb.MinY / 1000, maxX: bb.MaxX / 1000, maxY: bb.MaxY / 1000 };
    }),
    cores: room.cores.map((c) => ({ core_id: c.core_id, ...c.box })),
    office_area_m2: room.floor_area_m2,
  };
}
