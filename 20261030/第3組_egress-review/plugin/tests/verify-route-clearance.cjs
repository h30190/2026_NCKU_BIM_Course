// Recompute a saved export without modifying the snapshot or its historical reports.
// node plugin/tests/verify-route-clearance.cjs <snapshot.json> <new-output-directory>
const fs = require('node:fs');
const path = require('node:path');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const G = require('../../engine/geometry.js');
const { review } = require('../../engine/review.js');
const [input, output] = process.argv.slice(2);
if (!input || !output) throw new Error('Provide the saved snapshot and a separate output directory.');
const bytes = fs.readFileSync(input), snapshot = JSON.parse(bytes.toString('utf8'));
const before = JSON.stringify(snapshot), results = [];
for (const room of snapshot.rooms.filter((r) => r.is_habitable === true)) {
  const facts = G.measureRoom(room, snapshot);
  assert.deepEqual(G.measureRoom(room, snapshot), facts, `Non-deterministic: ${room.element_id}`);
  if (facts.missing) {
    assert.ok(!facts.missing.includes('幾何計算出錯'), facts.missing);
    results.push({ element_id: room.element_id, name: room.name, missing: facts.missing });
    continue;
  }
  assert.equal(facts.route_clearance_m, 0.45);
  assert.equal(facts.geometry_version, '0.3.2-clearance');
  assert.ok(Number.isFinite(facts.distance_exact_m));
  assert.ok(Math.abs(facts.distance_exact_m - G.plen(facts.path)) < 1e-8);
  assert.deepEqual(facts.path[0], facts.far_point);
  assert.deepEqual(facts.path.at(-1), snapshot.stairs.find((s) => s.element_id === facts.stair_id).entry_point);
  for (const c of facts.candidates.filter((c) => c.path)) {
    assert.equal(c.distance_m, G.r2(G.plen(c.path)));
    assert.deepEqual(c.path.at(-1), snapshot.stairs.find((s) => s.element_id === c.stair_id).entry_point);
  }
  results.push({ element_id: room.element_id, name: room.name, distance_exact_m: facts.distance_exact_m, facts });
}
assert.equal(JSON.stringify(snapshot), before, 'Input snapshot was mutated');
assert.deepEqual(fs.readFileSync(input), bytes, 'Saved snapshot was modified');
const settingsFile = input + '.settings.json';
const settings = fs.existsSync(settingsFile) ? JSON.parse(fs.readFileSync(settingsFile, 'utf8')) : {};
const pack = JSON.parse(fs.readFileSync(path.join(__dirname, '../../regs/rules.json'), 'utf8'));
const batch = review(snapshot, settings, pack, { batch_id: 'QA-ROUTE-CLEARANCE-20261105', created_at: snapshot.project.extracted_at });
for (const r of results.filter((r) => r.missing)) {
  const checked = batch.rooms.find((x) => x.element_id === r.element_id);
  assert.equal(checked.status, '模型資料不足'); assert.equal(checked.facts, null);
}
const computed = results.filter((r) => !r.missing);
const summary = { snapshot_sha256: crypto.createHash('sha256').update(bytes).digest('hex'),
  model: snapshot.project.model_name, level: snapshot.project.level.name, geometry_version: '0.3.2-clearance',
  route_clearance_m: 0.45, habitable_rooms: results.length, computed: computed.length,
  missing: results.length - computed.length, min_distance_m: computed.length ? Math.min(...computed.map((r) => r.distance_exact_m)) : null,
  max_distance_m: computed.length ? Math.max(...computed.map((r) => r.distance_exact_m)) : null,
  rooms: results.map(({ facts, ...r }) => r),
  scope: 'Recomputed saved export; not a new live Revit export or legal acceptance.' };
fs.mkdirSync(output, { recursive: true });
fs.writeFileSync(path.join(output, 'clearance-verification.json'), JSON.stringify(summary, null, 2));
fs.writeFileSync(path.join(output, 'clearance-batch.json'), JSON.stringify(batch, null, 2));
console.log(JSON.stringify({ ...summary, rooms: summary.rooms.map((r) => ({ element_id: r.element_id, name: r.name, distance_exact_m: r.distance_exact_m, missing: Boolean(r.missing) })) }, null, 2));
