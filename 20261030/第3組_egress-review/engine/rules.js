// 【C 負責】法規判定與設計提示。門檻只能來自 regs/ 官方原文與 regs/rules.json。
// 輸出對應 docs/data-contract.md 第 3 節 status、legal、hints、corridors[]
(function (root) {
  const G = typeof module !== "undefined" ? require("./geometry.js") : root.EgressGeometry;
  const GROUP_30 = ["A-1", "A-2", "B-1", "B-2", "B-3", "D-1"];
  const GROUP_C = ["C-1", "C-2"];
  const GROUP_CLASSROOM = ["D-3", "D-4", "D-5"];

  // §93 第 2 款：依使用類組與條件決定步行距離上限，並記錄套用了哪些規則
  function walkLimit(settings) {
    settings = settings || {};
    const g = settings.use_group;
    if (!g) return { limit: null, rules: [], pending: ["尚未選擇使用類組"] };
    const rules = ["R93-2-END"], pending = [];
    let limit;
    if (GROUP_30.includes(g)) { limit = 30; rules.push("R93-2-1A"); }
    else if (GROUP_C.includes(g)) {
      rules.push("R93-2-1C");
      if (settings.tv_studio_with_audience == null) pending.push("C 類：是否為有現場觀眾之電視攝影場");
      limit = settings.tv_studio_with_audience ? 30 : 70;
    } else { limit = 50; rules.push("R93-2-2"); }
    if (settings.floor_number == null) pending.push("尚未填樓層數（第 15 層以上的上限不同）");
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
    settings = settings || {};
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

  // 判定順序：資料不足 → 需人工確認 → 符合／不符合；設計提示另外列，不影響 status
  function judgeRoom(room, facts, lim, P, corridors) {
    room = room || {};
    facts = facts || {};
    lim = lim || { limit: null, rules: [], pending: ["法規步行距離上限尚未計算"] };
    P = P || {};
    corridors = Array.isArray(corridors) ? corridors : [];
    // 判定用未四捨五入的距離（整合規則第 12 條）
    const d = facts.distance_exact_m != null ? facts.distance_exact_m : facts.distance_m;
    const res = { status: null, reasons: [], legal: null, hints: [] };
    // 已標示非居室的房間不在 §93 檢討範圍，先處理，不因為算不出距離而列資料不足
    if (room.is_habitable === false && (facts.missing || !Number.isFinite(d))) {
      res.status = "需人工確認";
      res.reasons.push("模型標示為非居室，不列入 §93 居室檢討；請確認分類是否正確");
      return res;
    }
    if (facts.missing) { res.status = "模型資料不足"; res.reasons.push(facts.missing); return res; }
    if (!Number.isFinite(d) || d < 0) { res.status = "模型資料不足"; res.reasons.push("缺少有效的步行距離計算結果"); return res; }
    if (room.is_habitable === false) {
      res.status = "需人工確認";
      res.reasons.push("模型標示為非居室；請確認是否應排除 §93 居室檢討");
      return res;
    }
    if (room.is_habitable == null) res.reasons.push("是否為居室待確認（§93 只檢討居室）");
    // B 會把未確認的樓梯也算進來，這裡一律留給人確認，不下符合／不符合
    if (facts.stair_confirmed === false) res.reasons.push(`計算用的樓梯（${facts.stair_name || facts.stair_id}）尚未確認為直通樓梯`);
    if (String(facts.endpoint_source || "").startsWith("auto:")) res.reasons.push("樓梯第一階位置由梯段路徑自動推得，尚未人工確認");
    if (Array.isArray(lim.pending)) res.reasons.push(...lim.pending);
    if (lim.limit != null)
      res.legal = { rules: lim.rules, limit_m: lim.limit, diff_m: G.r2(d - lim.limit), pass: d <= lim.limit };
    res.status = res.reasons.length ? "需人工確認" : !res.legal ? "需人工確認" : res.legal.pass ? "符合" : "不符合";

    // 設計提示：專案保守值，不是法定門檻
    if (res.legal && res.legal.pass && Number.isFinite(P["P-NEAR"]) && d >= res.legal.limit_m * P["P-NEAR"])
      res.hints.push({ id: "P-NEAR", message: `已達法定上限的 ${Math.round(d / res.legal.limit_m * 100)}%（專案提示門檻 ${P["P-NEAR"] * 100}%）` });
    for (const t of facts.turns || []) {
      if (!t || !Array.isArray(t.at) || !Number.isFinite(t.deflection_deg)) continue;
      const c = G.corridorAt(corridors, t.at);
      if (c && Number.isFinite(P["P-TURN"]) && Number.isFinite(P["P-CLEAR"]) && t.deflection_deg >= P["P-TURN"] && c.clear_width_m != null && c.clear_width_m < P["P-CLEAR"])
        res.hints.push({ id: "P-TURN", at: t.at, message: `${c.name} 轉向 ${t.deflection_deg}°，淨寬 ${c.clear_width_m} m 小於專案值 ${P["P-CLEAR"]} m` });
    }
    return res;
  }

  function judgeCorridor(c, settings, habitableArea) {
    c = c || {};
    habitableArea = Number.isFinite(habitableArea) ? habitableArea : 0;
    const req = corridorRequirement(settings, habitableArea, c.rooms_both_sides);
    const out = { element_id: c.element_id, name: c.name, clear_width_m: c.clear_width_m, required_m: req.required, rule: req.rule, reasons: [...req.pending] };
    if (c.clear_width_m == null) { out.status = "模型資料不足"; out.reasons.push("走廊有效淨寬未標示"); }
    else if (out.reasons.length) out.status = "需人工確認";
    else out.status = c.clear_width_m >= req.required ? "符合" : "不符合";
    return out;
  }

  const api = { walkLimit, corridorRequirement, judgeRoom, judgeCorridor };
  if (typeof module !== "undefined") module.exports = api; else root.EgressRules = api;
})(this);
