// Verify actual Revit exports, including the stale-manual-endpoint regression.
// node plugin/tests/verify-merged-export.cjs <automatic.json> <stale-after-fix.json>
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const [automaticPath, stalePath] = process.argv.slice(2);
if (!automaticPath || !stalePath) throw new Error('Provide automatic and stale-endpoint Revit export paths.');
const root = path.resolve(__dirname, '../..');
const read = file => {
  const bytes = fs.readFileSync(file);
  assert.notDeepEqual([...bytes.subarray(0, 3)], [239, 187, 191], 'UTF-8 must have no BOM');
  return JSON.parse(bytes.toString('utf8'));
};
const automatic = read(automaticPath), stale = read(stalePath);
for (const snapshot of [automatic, stale]) {
  assert.equal(snapshot.project.unit, 'm');
  assert.equal(snapshot.project.has_unsaved_changes, false);
  assert.ok(snapshot.rooms.length > 0);
  assert.ok(!snapshot.warnings.some(w => w.code === 'ELEMENT_EXTRACTION_FAILED'));
  assert.ok(snapshot.rooms.every(r => typeof r.element_id === 'string'));
  const doorIds = new Set(snapshot.doors.map(d => d.element_id));
  assert.ok(snapshot.rooms.every(r => r.door_ids.every(id => doorIds.has(id))));
  assert.ok(snapshot.unresolved_doors.every(d => d.position === null));
  for (const room of snapshot.rooms.filter(r => r.boundary_closed && r.area_m2 > 0)) {
    const area = Math.abs(room.boundary.reduce((sum, p, i, points) => {
      const q = points[(i + 1) % points.length];
      return sum + p[0] * q[1] - q[0] * p[1];
    }, 0)) / 2;
    assert.ok(Math.abs(area - room.area_m2) < 1e-7);
  }
}
assert.equal(automatic.project.model_guid, stale.project.model_guid);
assert.equal(automatic.project.level_id, stale.project.level_id);
assert.equal(automatic.project.phase_id, stale.project.phase_id);
assert.deepEqual(automatic.rooms, stale.rooms);
assert.deepEqual(automatic.obstacles, stale.obstacles);
assert.ok(automatic.stairs.some(s => s.endpoint_source.startsWith('auto:') && s.is_direct_stair === null));
const staleIds = new Set(stale.warnings.filter(w => w.code === 'STAIR_ENDPOINT_STALE').map(w => w.element_id));
assert.ok(staleIds.size > 0, 'The stale-point fixture must exercise the validation failure');
for (const id of staleIds) {
  assert.ok(!stale.stairs.some(s => s.element_id === id), 'A stale explicit endpoint must not enter routing stairs');
  const unresolved = stale.unresolved_stairs.find(s => s.element_id === id);
  assert.ok(unresolved, 'A stale staircase must be retained as unresolved');
  assert.equal(unresolved.entry_point, null);
  assert.equal(unresolved.is_direct_stair, null, 'Do not inherit a prior true qualification');
  assert.equal(unresolved.endpoint_source, null);
}
const settings = {use_group:null, use_group_set_by:null, floor_number:null, below_ground:null,
  fire_resistant:null, tv_studio_with_audience:null, corridor_serves_classroom:null, duplex_housing:null};
const pack = read(path.join(root, 'regs/rules.json'));
const engine = require(path.join(root, 'engine/review.js'));
for (const snapshot of [automatic, stale]) {
  const batch = engine.review(snapshot, settings, pack, {created_at:snapshot.project.extracted_at});
  assert.ok(batch.rooms.every(r => r.status === '模型資料不足'));
}
const reportPath = automaticPath.replace(/\.json$/i, '-檢討網頁.html');
const html = fs.readFileSync(reportPath, 'utf8');
assert.ok(html.includes(automatic.project.model_name));
assert.ok(!html.includes('/*G3_IMPORT*/null'));
assert.ok(!html.includes('/*G3_SETTINGS*/null'));
const result = {result:'pass', model:automatic.project.model_name, level:automatic.project.level.name,
  counts:{rooms:automatic.rooms.length, valid_rooms:automatic.rooms.filter(r => r.boundary_closed && r.area_m2 > 0).length,
    doors:automatic.doors.length, unresolved_doors:automatic.unresolved_doors.length, stairs:automatic.stairs.length,
    corridors:automatic.corridors.length, warnings:automatic.warnings.length},
  automatic_endpoints:automatic.stairs.map(s => ({element_id:s.element_id,entry_point:s.entry_point,
    endpoint_source:s.endpoint_source,is_direct_stair:s.is_direct_stair})),
  stale_stair_ids:[...staleIds],
  hashes:Object.fromEntries([automaticPath,stalePath,reportPath].map(file => [path.basename(file),
    crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex')]))};
console.log(JSON.stringify(result, null, 2));
