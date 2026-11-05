// 【共用，改之前先在群組講】把 B 的幾何和 C 的規則串成一個檢討批次。
// 輸入：模型快照（A）＋專案設定（使用者手選）＋規則包（regs/rules.json）；輸出：檢討批次（D 讀）
(function (root) {
  const node = typeof module !== "undefined";
  const G = node ? require("./geometry.js") : root.EgressGeometry;
  const R = node ? require("./rules.js") : root.EgressRules;
  const ENGINE_VERSION = "0.2.0";

  // FNV-1a，只用來辨識「這批結果是用哪一份快照算的」
  function hash(obj) {
    const s = JSON.stringify(obj);
    let h = 0x811c9dc5;
    for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
    return "fnv1a-" + h.toString(16).padStart(8, "0");
  }

  function review(snapshot, settings, pack, opts = {}) {
    const P = Object.fromEntries(pack.project_checks.items.map((i) => [i.id, i.value]));
    const lim = R.walkLimit(settings);
    const habitableArea = snapshot.rooms.filter((r) => r.is_habitable === true).reduce((s, r) => s + (r.area_m2 || 0), 0);

    const rooms = snapshot.rooms.map((room) => {
      const facts = G.measureRoom(room, snapshot);
      const j = R.judgeRoom(room, facts, lim, P, snapshot.corridors);
      return {
        element_id: room.element_id, unique_id: room.unique_id, number: room.number, name: room.name,
        status: j.status, reasons: j.reasons, facts: facts.missing ? null : facts, legal: j.legal, hints: j.hints,
        manual_review: { status: "未覆核", by: null, note: null },
      };
    });
    const corridors = snapshot.corridors.map((c) => R.judgeCorridor(c, settings, habitableArea));
    const fixList = [
      ...snapshot.warnings.map((w) => ({ element_id: w.element_id, issue: w.message, source: "擷取檢查" })),
      ...snapshot.rooms.filter((r) => r.is_habitable == null).map((r) => ({ element_id: r.element_id, issue: `${r.name}：是否為居室待確認`, source: "使用者確認" })),
    ];

    return {
      batch_id: opts.batch_id || "B-" + hash({ snapshot, settings, v: pack.version }).slice(6),
      created_at: opts.created_at || new Date().toISOString(),
      locked: false,
      inputs: {
        snapshot_hash: hash(snapshot), model: snapshot.project.model_name, level: snapshot.project.level.name,
        extracted_at: snapshot.project.extracted_at, rule_pack: pack.pack_id + "@" + pack.version, rule_pack_status: pack.status,
        engine_version: ENGINE_VERSION, settings, habitable_area_m2: G.r2(habitableArea),
      },
      rooms, corridors, fix_list: fixList,
    };
  }

  const api = { review, hash, ENGINE_VERSION };
  if (node) module.exports = api; else root.EgressEngine = api;
})(this);
