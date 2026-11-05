// 逃生動線檢討引擎（參考實作）：純函式，同樣輸入一定得到同樣結果，不呼叫語言模型。
// 輸入：模型快照（A 外掛輸出）＋專案設定（使用者手選）＋規則包（regs/rules.json）
// 輸出：檢討批次（給前端與報告用）
(function (root) {
  const ENGINE_VERSION = "0.2.0";
  const GROUP_30 = ["A-1", "A-2", "B-1", "B-2", "B-3", "D-1"];
  const GROUP_C = ["C-1", "C-2"];
  const GROUP_CLASSROOM = ["D-3", "D-4", "D-5"];

  const dist = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);
  const plen = (pts) => pts.slice(1).reduce((s, p, i) => s + dist(pts[i], p), 0);
  const r2 = (n) => Math.round(n * 100) / 100;

  // FNV-1a，只用來辨識「這批結果是用哪一份快照算的」
  function hash(obj) {
    const s = JSON.stringify(obj);
    let h = 0x811c9dc5;
    for (let i = 0; i < s.length; i++) { h ^= s.charCodeAt(i); h = Math.imul(h, 0x01000193) >>> 0; }
    return "fnv1a-" + h.toString(16).padStart(8, "0");
  }

  // §93 第 2 款：依使用類組與條件決定步行距離上限，並記錄套用了哪些規則
  function walkLimit(settings) {
    const g = settings.use_group;
    if (!g) return { limit: null, rules: [], pending: ["尚未選擇使用類組"] };
    const rules = [], pending = [];
    let limit;
    if (GROUP_30.includes(g)) { limit = 30; rules.push("R93-2-1A"); }
    else if (GROUP_C.includes(g)) {
      rules.push("R93-2-1C");
      if (settings.tv_studio_with_audience == null) pending.push("C 類：是否為有現場觀眾之電視攝影場");
      limit = settings.tv_studio_with_audience ? 30 : 70;
    } else { limit = 50; rules.push("R93-2-2"); }
    if (settings.floor_number >= 15) {
      rules.push("R93-2-3");
      if (limit === 30) limit = 20;
      else if (limit === 50) limit = 40;
      else pending.push("第 15 層以上：條文未規定 70 m 如何折減");
    }
    if (settings.fire_resistant == null) pending.push("是否為防火構造或使用不燃材料");
    if (settings.fire_resistant === false) { rules.push("R93-2-5"); limit = Math.min(limit, 30); }
    if (settings.duplex_housing) pending.push("複層式集合住宅（R93-2-4 未實作）");
    return { limit, rules, pending };
  }

  // §92 第 1 款：走廊淨寬需求
  function corridorRequirement(settings, habitableArea, twoSided) {
    const g = settings.use_group;
    if (!g) return { required: null, rule: null, pending: ["尚未選擇使用類組"] };
    if (GROUP_CLASSROOM.includes(g)) {
      if (settings.corridor_serves_classroom == null) return { required: twoSided ? 2.4 : 1.8, rule: "R92-1-1", pending: ["走廊是否供教室使用"] };
      if (settings.corridor_serves_classroom) return { required: twoSided ? 2.4 : 1.8, rule: "R92-1-1", pending: [] };
    }
    if (g === "F-1") return { required: twoSided ? 1.6 : 1.2, rule: "R92-1-2", pending: [] };
    const big = habitableArea >= (settings.below_ground ? 100 : 200);
    return { required: big && twoSided ? 1.6 : 1.2, rule: "R92-1-3", pending: [] };
  }

  // 房間內最遠點：外框角點往內縮 0.5 m 後，取離門最遠者（第一階段假設房內無固定障礙物）
  function farPoint(boundary, door) {
    const cx = boundary.reduce((s, p) => s + p[0], 0) / boundary.length;
    const cy = boundary.reduce((s, p) => s + p[1], 0) / boundary.length;
    const inset = boundary.map(([x, y]) => [x + Math.sign(cx - x) * 0.5, y + Math.sign(cy - y) * 0.5]);
    return inset.reduce((a, b) => (dist(b, door) > dist(a, door) ? b : a));
  }

  // 可行路徑：最遠點 → 門 → 垂直接到走廊中心線 → 沿中心線 → 樓梯計算終點
  function routeTo(far, door, stair, corridorY) {
    const pts = [far, door, [door[0], corridorY], [stair.entry_point[0], corridorY], stair.entry_point];
    return pts.filter((p, i) => i === 0 || dist(p, pts[i - 1]) > 1e-9);
  }

  // 每個中間點的轉向角（0＝直行，90＝直角轉彎，180＝迴轉）
  function turns(path) {
    const out = [];
    for (let i = 1; i < path.length - 1; i++) {
      const a = [path[i][0] - path[i - 1][0], path[i][1] - path[i - 1][1]];
      const b = [path[i + 1][0] - path[i][0], path[i + 1][1] - path[i][1]];
      const cos = (a[0] * b[0] + a[1] * b[1]) / (Math.hypot(...a) * Math.hypot(...b));
      out.push({ at: path[i], deflection_deg: r2(Math.acos(Math.max(-1, Math.min(1, cos))) * 180 / Math.PI) });
    }
    return out;
  }

  function corridorAt(corridors, p) {
    return corridors.find((c) => {
      const [[x0, y0], [x1, y1]] = c.centerline;
      return Math.abs(p[1] - y0) < 1e-6 && p[0] >= Math.min(x0, x1) - 1e-6 && p[0] <= Math.max(x0, x1) + 1e-6;
    });
  }

  function review(snapshot, settings, pack, opts = {}) {
    const P = Object.fromEntries(pack.project_checks.items.map((i) => [i.id, i.value]));
    const corridorY = snapshot.corridors[0].centerline[0][1];
    const stairs = snapshot.stairs.filter((s) => s.is_direct_stair === true);
    const doorById = Object.fromEntries(snapshot.doors.map((d) => [d.element_id, d]));
    const lim = walkLimit(settings);
    const habitableArea = snapshot.rooms.filter((r) => r.is_habitable === true).reduce((s, r) => s + (r.area_m2 || 0), 0);

    const rooms = snapshot.rooms.map((room) => {
      const res = { element_id: room.element_id, unique_id: room.unique_id, number: room.number, name: room.name,
        status: null, reasons: [], facts: null, legal: null, hints: [], manual_review: { status: "未覆核", by: null, note: null } };
      if (!room.boundary_closed) { res.status = "模型資料不足"; res.reasons.push("房間邊界未封閉，無法決定居室範圍"); return res; }
      const door = doorById[room.door_ids[0]];
      if (!door) { res.status = "模型資料不足"; res.reasons.push("找不到房間的門"); return res; }
      if (!stairs.length) { res.status = "模型資料不足"; res.reasons.push("沒有已確認的直通樓梯"); return res; }

      const far = farPoint(room.boundary, door.position);
      let best = null;
      const candidates = [];
      for (const s of stairs) {
        const path = routeTo(far, door.position, s, corridorY);
        const d = plen(path);
        candidates.push({ stair_id: s.element_id, stair_name: s.name, distance_m: r2(d) });
        if (!best || d < best.d) best = { s, path, d };
      }
      const indoor = dist(far, door.position);
      res.facts = {
        far_point: far, path: best.path, stair_id: best.s.element_id, stair_name: best.s.name,
        endpoint_type: best.s.endpoint_type, indoor_m: r2(indoor), corridor_m: r2(best.d - indoor), distance_m: r2(best.d),
        turns: turns(best.path), candidates,
      };

      if (room.is_habitable == null) res.reasons.push("是否為居室待確認（§93 只檢討居室）");
      if (lim.pending.length) res.reasons.push(...lim.pending);
      if (lim.limit != null) {
        res.legal = { rules: lim.rules, limit_m: lim.limit, diff_m: r2(best.d - lim.limit), pass: best.d <= lim.limit };
      }
      if (res.reasons.length) res.status = "需人工確認";
      else res.status = res.legal.pass ? "符合" : "不符合";

      // 設計提示：專案保守值，不是法定門檻
      if (res.legal && res.legal.pass && best.d >= res.legal.limit_m * P["P-NEAR"])
        res.hints.push({ id: "P-NEAR", message: `已達法定上限的 ${Math.round(best.d / res.legal.limit_m * 100)}%（專案提示門檻 ${P["P-NEAR"] * 100}%）` });
      for (const t of res.facts.turns) {
        const c = corridorAt(snapshot.corridors, t.at);
        if (c && t.deflection_deg >= P["P-TURN"] && c.clear_width_m != null && c.clear_width_m < P["P-CLEAR"])
          res.hints.push({ id: "P-TURN", at: t.at, message: `${c.name} 轉向 ${t.deflection_deg}°，淨寬 ${c.clear_width_m} m 小於專案值 ${P["P-CLEAR"]} m` });
      }
      return res;
    });

    const corridors = snapshot.corridors.map((c) => {
      const req = corridorRequirement(settings, habitableArea, c.rooms_both_sides);
      const out = { element_id: c.element_id, name: c.name, clear_width_m: c.clear_width_m, required_m: req.required, rule: req.rule, reasons: [...req.pending] };
      if (c.clear_width_m == null) { out.status = "模型資料不足"; out.reasons.push("走廊有效淨寬未標示"); }
      else if (out.reasons.length) out.status = "需人工確認";
      else out.status = c.clear_width_m >= req.required ? "符合" : "不符合";
      return out;
    });

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
        engine_version: ENGINE_VERSION, settings, habitable_area_m2: r2(habitableArea),
      },
      rooms, corridors, fix_list: fixList,
    };
  }

  const api = { review, walkLimit, corridorRequirement, hash, ENGINE_VERSION };
  if (typeof module !== "undefined") module.exports = api; else root.EgressEngine = api;
})(this);
