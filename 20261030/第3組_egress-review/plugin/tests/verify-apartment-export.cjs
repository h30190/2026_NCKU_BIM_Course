const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const root = path.resolve(__dirname, '../..');
const file = process.argv[2];
if (!file) throw new Error('Provide the apartment Level 4 Revit export path.');
const outputDirectory = path.dirname(path.resolve(file));
const bytes = fs.readFileSync(file);
assert.notDeepEqual([...bytes.subarray(0,3)], [239,187,191]);
const snapshot = JSON.parse(bytes.toString('utf8'));
assert.equal(snapshot.project.model_name, 'apartment-project-revit-ASSIGHMENT');
assert.equal(snapshot.project.level_id, '390158');
assert.equal(snapshot.project.phase_id, '110001');
assert.equal(snapshot.project.unit, 'm');
assert.equal(snapshot.project.has_unsaved_changes, false);
assert.ok(Math.abs(snapshot.project.level.elevation_m - 9.5) < 1e-10);
assert.equal(snapshot.rooms.length, 31);
assert.equal(snapshot.doors.length, 39);
assert.equal(snapshot.unresolved_doors.length, 0);
assert.equal(snapshot.stairs.length, 2);
assert.equal(snapshot.unresolved_stairs.length, 0);
assert.ok(!snapshot.warnings.some(w => w.code === 'ELEMENT_EXTRACTION_FAILED'));
const roomIds = new Set(snapshot.rooms.map(r => r.element_id));
const doorIds = new Set(snapshot.doors.map(d => d.element_id));
let maxAreaDifference = 0;
const areaDifferences = [];
for (const room of snapshot.rooms) {
  assert.equal(typeof room.element_id, 'string');
  assert.equal(room.boundary_closed, true);
  assert.ok(room.area_m2 > 0);
  const area = Math.abs(room.boundary.reduce((sum, p, i, points) => {
    const q = points[(i + 1) % points.length];
    return sum + p[0] * q[1] - q[0] * p[1];
  }, 0)) / 2;
  maxAreaDifference = Math.max(maxAreaDifference, Math.abs(area - room.area_m2));
  const difference = Math.abs(area - room.area_m2);
  // Revit's reported area and the exported polyline are not bit-identical.
  // This is a data consistency tolerance, not a legal or measurement threshold.
  assert.ok(difference < 0.001 && difference / room.area_m2 < 0.0001);
  if (difference > 1e-7) areaDifferences.push({element_id:room.element_id,difference_m2:difference});
  assert.ok(room.door_ids.every(id => doorIds.has(id)));
}
for (const door of snapshot.doors) {
  assert.equal(typeof door.element_id, 'string');
  assert.ok(door.position.length === 2 && door.position.every(Number.isFinite));
  assert.ok(door.room_ids.every(id => roomIds.has(id)));
  for (const id of door.room_ids) assert.ok(snapshot.rooms.find(r => r.element_id === id).door_ids.includes(door.element_id));
}
for (const stair of snapshot.stairs) {
  assert.equal(stair.is_direct_stair, null);
  assert.ok(stair.endpoint_source.startsWith('auto:'));
  assert.ok(stair.entry_point.every(Number.isFinite));
}
const settings = {use_group:'H-1',use_group_set_by:'QA placeholder, not confirmed',floor_number:4,
  below_ground:false,fire_resistant:null,tv_studio_with_audience:null,corridor_serves_classroom:null,duplex_housing:false};
const pack = JSON.parse(fs.readFileSync(path.join(root,'regs/rules.json'),'utf8'));
const batch = require(path.join(root,'engine/review.js')).review(snapshot, settings, pack,
  {batch_id:'QA-APARTMENT-L4-20261102',created_at:snapshot.project.extracted_at});
const habitableIds = new Set(snapshot.rooms.filter(r => r.is_habitable === true).map(r => r.element_id));
assert.equal(habitableIds.size, 20);
assert.equal(snapshot.rooms.filter(r => r.is_circulation === true).length, 1);
const reviewed = batch.rooms.filter(r => habitableIds.has(r.element_id));
assert.equal(reviewed.length, 20);
assert.ok(reviewed.every(r => r.facts && Number.isFinite(r.facts.distance_exact_m) && r.facts.path.length > 1));
assert.ok(reviewed.every(r => r.status === '需人工確認'));
for (const room of reviewed) {
  assert.ok(room.facts.candidates.length === 2);
  assert.equal(room.facts.stair_confirmed, false);
  assert.ok(room.facts.endpoint_source.startsWith('auto:'));
  assert.ok(room.reasons.some(reason => reason.includes('直通樓梯')));
}
fs.writeFileSync(path.join(outputDirectory,'apartment-Level4-batch.json'),JSON.stringify(batch,null,2));
const distances = reviewed.map(r => r.facts.distance_exact_m);
const summary = {result:'pass',model:snapshot.project.model_name,level:snapshot.project.level.name,
  extracted_at:snapshot.project.extracted_at,rooms:31,habitable:20,non_habitable:10,circulation:1,
  doors:39,stairs:2,unresolved_doors:0,corridor_centerlines:0,
  missing_door_clear_width:snapshot.doors.filter(d => d.clear_width_m == null).length,
  warnings:snapshot.warnings.length,max_area_difference_m2:maxAreaDifference,area_differences:areaDifferences,
  computed_habitable_rooms:reviewed.length,min_distance_m:Math.min(...distances),max_distance_m:Math.max(...distances),
  statuses:reviewed.map(r => ({element_id:r.element_id,name:r.name,status:r.status,distance_m:r.facts.distance_m,
    stair_id:r.facts.stair_id,reasons:r.reasons})),
  settings_note:'H-1 is a functional test placeholder; fire resistance and both staircase qualifications remain unconfirmed.',
  sha256:crypto.createHash('sha256').update(bytes).digest('hex')};
fs.writeFileSync(path.join(outputDirectory,'apartment-verification.json'),JSON.stringify(summary,null,2));
console.log(JSON.stringify(summary,null,2));
