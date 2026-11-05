// 【B 負責】幾何：最遠點、可行路徑、距離、候選出口、轉向角。純函式，不呼叫語言模型。
// 輸出對應 docs/data-contract.md 第 3 節 rooms[].facts
(function (root) {
  const dist = (a, b) => Math.hypot(b[0] - a[0], b[1] - a[1]);
  const plen = (pts) => pts.slice(1).reduce((s, p, i) => s + dist(pts[i], p), 0);
  const r2 = (n) => Math.round(n * 100) / 100;

  const CLEARANCE = 0.45; // 整段行走路線離牆／固定障礙物至少 0.45 m（使用者指定的專案設定，不是法規數字）
  const GEOMETRY_VERSION = "0.3.2-clearance";
  const STEP = 0.25; // 沿內縮外框每 0.25 m 取一個候選點，再在可能更遠的區間細找

  // 外框每條邊往房內平移 d，相鄰兩條平移線的交點就是內縮後的角點（凹角、斜牆都適用，不管點的順序）
  function insetPolygon(poly, d) {
    const pts = poly.filter((p, i) => dist(p, poly[(i + 1) % poly.length]) > EPS);
    const n = pts.length;
    const area = pts.reduce((s, p, i) => { const q = pts[(i + 1) % n]; return s + p[0] * q[1] - q[0] * p[1]; }, 0);
    const k = area > 0 ? d : -d; // 逆時針時房內在每條邊的左手邊
    const lines = pts.map((p, i) => {
      const q = pts[(i + 1) % n], L = dist(p, q), nx = (-(q[1] - p[1]) / L) * k, ny = ((q[0] - p[0]) / L) * k;
      return [[p[0] + nx, p[1] + ny], [q[0] + nx, q[1] + ny]];
    });
    return lines.map((l, i) => {
      const [a, b] = lines[(i - 1 + n) % n], [c, e] = l;
      const r = [b[0] - a[0], b[1] - a[1]], s = [e[0] - c[0], e[1] - c[1]], den = r[0] * s[1] - r[1] * s[0];
      if (Math.abs(den) < EPS) return c; // 兩條邊共線
      const t = ((c[0] - a[0]) * s[1] - (c[1] - a[1]) * s[0]) / den;
      return [a[0] + t * r[0], a[1] + t * r[1]];
    });
  }

  // 相容舊呼叫；內縮是可行路線範圍，不再單獨縮起點後沿原外框排路。
  function insetCorners(boundary) {
    return insetPolygon(boundary, CLEARANCE);
  }

  // 只有一扇門時的最遠點：離門直線距離最遠的候選點
  function farPoint(boundary, door) {
    return insetCorners(boundary).reduce((a, b) => (dist(b, door) > dist(a, door) ? b : a));
  }

  const EPS = 1e-6;
  const SIDE = 1e-4; // 判斷貼邊線段兩側是否為空地時往外看的距離（0.1 mm），數值容差，不是法規數字
  const SNAP = 0.05; // 中心線端點相距 5 cm 內視為接在一起（模型繪製容差，自訂假設，不是法規數字）

  // 點 p 在線段 ab 上最近的點
  function closestOnSeg(p, a, b) {
    const dx = b[0] - a[0], dy = b[1] - a[1], L = dx * dx + dy * dy;
    const t = L < EPS ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * dx + (p[1] - a[1]) * dy) / L));
    return [a[0] + t * dx, a[1] + t * dy];
  }

  // 兩線段的交點（平行或不相交回傳 null）
  function segCross(a, b, c, d) {
    const r = [b[0] - a[0], b[1] - a[1]], s = [d[0] - c[0], d[1] - c[1]], den = r[0] * s[1] - r[1] * s[0];
    if (Math.abs(den) < EPS) return null;
    const t = ((c[0] - a[0]) * s[1] - (c[1] - a[1]) * s[0]) / den, u = ((c[0] - a[0]) * r[1] - (c[1] - a[1]) * r[0]) / den;
    return t >= -EPS && t <= 1 + EPS && u >= -EPS && u <= 1 + EPS ? [a[0] + t * r[0], a[1] + t * r[1]] : null;
  }

  // 走廊網路：所有中心線（可多段折線）切成線段，在交叉、T 接、門和樓梯的接點切開，連成圖
  // attach 是要接進網路的點（門、樓梯計算終點），各自垂直接到最近的中心線；polys 的邊和中心線交叉處也切開
  function corridorNetwork(corridors, attach, polys = []) {
    const segs = corridors.flatMap((c) => c.centerline.slice(1).map((p, i) => [c.centerline[i], p]));
    const cuts = segs.map(([a, b]) => [a, b]);
    segs.forEach((s, i) => segs.forEach((t, j) => {
      if (i === j) return;
      for (const e of s) { const q = closestOnSeg(e, t[0], t[1]); if (dist(e, q) < SNAP) cuts[j].push(q); }
      const x = j > i && segCross(s[0], s[1], t[0], t[1]); if (x) { cuts[i].push(x); cuts[j].push(x); }
    }));
    segs.forEach((s, i) => polys.forEach((poly) => poly.forEach((c, k) => {
      const x = segCross(s[0], s[1], c, poly[(k + 1) % poly.length]); if (x) cuts[i].push(x);
    })));
    const foot = attach.map((p) => {
      if (!segs.length) return null;
      let k = 0;
      segs.forEach((s, i) => { if (dist(p, closestOnSeg(p, ...s)) < dist(p, closestOnSeg(p, ...segs[k])) - EPS) k = i; });
      const q = closestOnSeg(p, ...segs[k]);
      cuts[k].push(q);
      return q;
    });

    const nodes = [];
    const nodeOf = (p) => {
      let i = nodes.findIndex((n) => dist(n, p) < SNAP);
      if (i < 0) { i = nodes.length; nodes.push(p); }
      return i;
    };
    const adj = [];
    segs.forEach(([a], i) => {
      const ids = cuts[i].sort((p, q) => dist(a, p) - dist(a, q)).map(nodeOf);
      for (let k = 1; k < ids.length; k++) {
        const u = ids[k - 1], v = ids[k];
        if (u === v) continue;
        const w = dist(nodes[u], nodes[v]);
        (adj[u] = adj[u] || []).push([v, w]);
        (adj[v] = adj[v] || []).push([u, w]);
      }
    });
    return { nodes, adj, nodeOf, foot: foot.map((q) => (q ? nodeOf(q) : null)) };
  }

  // 可行通道圖：走廊中心線網路＋通行空間（is_circulation 為 true 的房間，例如大廳，在裡面可以直線走、繞柱子）
  // 門的另一側是通行空間 → 門直接接進那個空間；否則垂直接到最近的走廊中心線。樓梯計算終點落在通行空間裡也一樣。
  function walkNetwork(corridors, spaces, doors, stairs, obstacles) {
    const ends = [
      ...doors.map((d) => ({ p: d.position, sps: spaces.filter((s) => (d.room_ids || []).includes(s.element_id)) })),
      // 樓梯終點在空間裡就直接用；不在任何空間裡（例如防火門的插入點在牆裡），才和門一樣接到旁邊空間的外框
      ...stairs.map((st) => {
        const inside = spaces.filter((s) => inPoly(st.entry_point, s.boundary) >= 0);
        return { p: st.entry_point, sps: inside.length ? inside : spaces.filter((s) => gateOn(st.entry_point, s.boundary)) };
      }),
    ];
    const viaCorridor = ends.filter((e) => !e.sps.length);
    const net = corridorNetwork(corridors, viaCorridor.map((e) => e.p), spaces.flatMap((s) => [s.boundary, insetPolygon(s.boundary, CLEARANCE)]));
    // 中心線只能依已提供的淨寬檢查，不假裝快照有未擷取的牆。進入已知空間的部分仍逐段驗證牆距。
    const corridorEdge = (a, b) => {
      if (!awayFrom(a, b, obstacles) || obstacles.some((o) => inPoly(a, o) >= 0 || inPoly(b, o) >= 0)) return false;
      const mid = [(a[0] + b[0]) / 2, (a[1] + b[1]) / 2];
      const containing = spaces.filter((s) => inPoly(mid, s.boundary) >= 0);
      if (containing.length) return containing.some((s) => canWalk(a, b, s.boundary, obstacles));
      const c = corridorAt(corridors, mid);
      const margin = c && c.clear_width_m / 2 - CLEARANCE;
      return c && Number.isFinite(c.clear_width_m) && margin >= -EPS && c.centerline.slice(1).some((q, i) =>
        dist(a, closestOnSeg(a, c.centerline[i], q)) <= margin + EPS && dist(b, closestOnSeg(b, c.centerline[i], q)) <= margin + EPS) &&
        spaces.every((s) => awayFrom(a, b, [s.boundary]));
    };
    net.adj = net.nodes.map((a, i) => (net.adj[i] || []).filter(([j]) => corridorEdge(a, net.nodes[j])));
    // 不用 5 cm 合併淨距節點，否則門、內縮轉角可能被吸到牆邊。
    const nodeOf = (p) => {
      let i = net.nodes.findIndex((q) => dist(p, q) < EPS);
      if (i < 0) { i = net.nodes.length; net.nodes.push(p); net.adj.push([]); }
      return i;
    };
    const node = ends.map((e) => nodeOf(e.p));
    const link = (u, v) => {
      if (u === v) return;
      const w = dist(net.nodes[u], net.nodes[v]);
      (net.adj[u] = net.adj[u] || []).push([v, w]);
      (net.adj[v] = net.adj[v] || []).push([u, w]);
    };
    const portals = [];
    // 房間兩側各有 CLEARANCE 接入點；只有指定門／樓梯能透過短接入段穿越邊界。
    for (const s of spaces) {
      routeRegion(s.boundary, obstacles).nodes.forEach(nodeOf);
      ends.forEach((e, i) => {
        if (!e.sps.includes(s)) return;
        for (const anchor of entryAnchors(e.p, s.boundary, obstacles)) {
          const k = nodeOf(anchor); link(node[i], k);
          if (k !== node[i]) portals.push([e.p, anchor]);
        }
      });
    }
    for (const s of spaces) {
      const ids = net.nodes.map((p, i) => (clearPoint(p, s.boundary, obstacles) ? i : -1)).filter((i) => i >= 0);
      for (let a = 0; a < ids.length; a++) for (let b = a + 1; b < ids.length; b++)
        if (canWalk(net.nodes[ids[a]], net.nodes[ids[b]], s.boundary, obstacles)) link(ids[a], ids[b]);
    }
    viaCorridor.forEach((e, i) => {
      const foot = net.foot[i];
      if (foot == null) return;
      const p = net.nodes[foot], c = corridorAt(corridors, p), id = node[ends.indexOf(e)];
      if (!c || !Number.isFinite(c.clear_width_m) || c.clear_width_m < 2 * CLEARANCE - EPS ||
          dist(e.p, p) > c.clear_width_m / 2 + DOOR_WALL_MAX || !awayFrom(e.p, p, obstacles) ||
          obstacles.some((o) => inPoly(e.p, o) >= 0 || inPoly(p, o) >= 0) ||
          spaces.some((s) => !awayFrom(e.p, p, [s.boundary]))) return;
      link(id, foot);
      if (id !== foot) portals.push([e.p, p]);
    });
    return { nodes: net.nodes, adj: net.adj, doorNode: node.slice(0, doors.length), stairNode: node.slice(doors.length), portals };
  }

  // 門的座標在牆裡時，取房間外框上最近的點當進出口；已在房內或邊上就用原座標。太遠回傳 null（不是這間房的門）
  const DOOR_WALL_MAX = 0.6; // 牆心到牆面的容許距離，自訂假設，不是法規數字
  const HOST_ANGLE = Math.sin(0.1 * Math.PI / 180), HOST_OFFSET = 0.005; // 宿主牆分段容差：0.1°、5 mm，不放寬路線淨距
  function gateOn(p, poly) {
    if (inPoly(p, poly) >= 0) return p;
    let q = null;
    poly.forEach((a, i) => {
      const c = closestOnSeg(p, a, poly[(i + 1) % poly.length]);
      if (!q || dist(p, c) < dist(p, q)) q = c;
    });
    return dist(p, q) <= DOOR_WALL_MAX ? q : null;
  }

  // 這間房走得到的空間：所有通行空間，加上從這間房經由門一路連到的居室（例如臥室出去先經過客廳）
  // 碰到通行空間就不再往外找，路線才不會穿過別戶的居室；標成非居室、未確認的房間不當路線
  function walkableSpaces(room, snapshot) {
    const rooms = snapshot.rooms || [];
    const ok = (r) => r.boundary_closed && Array.isArray(r.boundary) && r.boundary.length >= 3 && r.element_id !== room.element_id;
    const out = new Set(rooms.filter((r) => r.is_circulation === true && ok(r)));
    const seen = new Set([room.element_id]), queue = [room.element_id];
    while (queue.length) {
      const cur = queue.shift();
      for (const d of snapshot.doors || []) {
        if (!(d.room_ids || []).includes(cur)) continue;
        for (const id of d.room_ids) {
          if (seen.has(id)) continue;
          seen.add(id);
          const r = rooms.find((x) => x.element_id === id);
          if (r && r.is_habitable === true && r.is_circulation !== true && ok(r)) { out.add(r); queue.push(id); }
        }
      }
    }
    return rooms.filter((r) => out.has(r));
  }

  // 網路上兩節點間最短的一串點（Dijkstra）；走不到回傳 null
  function netPath(net, from, to) {
    const n = net.nodes.length, d = Array(n).fill(Infinity), prev = Array(n).fill(-1), done = Array(n).fill(false);
    d[from] = 0;
    for (;;) {
      let u = -1;
      for (let i = 0; i < n; i++) if (!done[i] && d[i] < Infinity && (u < 0 || d[i] < d[u])) u = i;
      if (u < 0) return null;
      if (u === to) break;
      done[u] = true;
      for (const [v, w] of net.adj[u] || []) if (d[u] + w < d[v] - EPS) { d[v] = d[u] + w; prev[v] = u; }
    }
    const pts = [];
    for (let u = to; u >= 0; u = prev[u]) pts.unshift(net.nodes[u]);
    return pts;
  }

  // 每個節點到終點的最短距離；init[i] 是節點 i 的起始成本（不是起點填 Infinity）
  function distAll(adj, init) {
    const d = init.slice(), done = d.map(() => false);
    for (;;) {
      let u = -1;
      for (let i = 0; i < d.length; i++) if (!done[i] && d[i] < Infinity && (u < 0 || d[i] < d[u])) u = i;
      if (u < 0) return d;
      done[u] = true;
      for (const [v, w] of adj[u]) if (d[u] + w < d[v]) d[v] = d[u] + w;
    }
  }

  // 拿掉重複點和直行中間點，轉向角才不會多出 0°
  function simplify(pts) {
    const out = pts.filter((p, i) => i === 0 || dist(p, pts[i - 1]) > 1e-9);
    return out.filter((p, i) => {
      if (i === 0 || i === out.length - 1) return true;
      const a = out[i - 1], b = out[i + 1];
      const cross = (p[0] - a[0]) * (b[1] - p[1]) - (p[1] - a[1]) * (b[0] - p[0]);
      const dot = (p[0] - a[0]) * (b[0] - p[0]) + (p[1] - a[1]) * (b[1] - p[1]);
      return Math.abs(cross) > 1e-9 * Math.max(EPS, dist(a, b)) || dot < 0;
    });
  }

  // 點在多邊形內 1、邊上 0、外面 -1（射線法，不管點的順序）
  function inPoly(p, poly) {
    if (poly.some((a, i) => dist(p, closestOnSeg(p, a, poly[(i + 1) % poly.length])) < EPS)) return 0;
    let inside = false;
    for (let i = 0, j = poly.length - 1; i < poly.length; j = i++) {
      const [xi, yi] = poly[i], [xj, yj] = poly[j];
      if (yi > p[1] !== yj > p[1] && p[0] < ((xj - xi) * (p[1] - yi)) / (yj - yi) + xi) inside = !inside;
    }
    return inside ? 1 : -1;
  }

  // 線段 ab 能不能直接走：在每個碰到邊或角點的地方切開，每一小段的中點都要在房內、不在障礙物裡
  function inFreeSpace(a, b, room, obstacles) {
    const L = dist(a, b);
    if (L < EPS) return true;
    const t = (x) => ((x[0] - a[0]) * (b[0] - a[0]) + (x[1] - a[1]) * (b[1] - a[1])) / (L * L);
    const ts = [0, 1];
    for (const poly of [room, ...obstacles]) poly.forEach((c, i) => {
      const d = poly[(i + 1) % poly.length];
      const x = segCross(a, b, c, d); if (x) ts.push(t(x));
      for (const v of [c, d]) if (dist(v, closestOnSeg(v, a, b)) < EPS) ts.push(t(v));
    });
    ts.sort((p, q) => p - q);
    for (let i = 1; i < ts.length; i++) {
      if (ts[i] - ts[i - 1] < EPS) continue;
      const m = (ts[i] + ts[i - 1]) / 2, mp = [a[0] + m * (b[0] - a[0]), a[1] + m * (b[1] - a[1])];
      const inRoom = inPoly(mp, room), inObs = obstacles.map((o) => inPoly(mp, o));
      if (inRoom < 0 || inObs.some((v) => v > 0)) return false;
      // 同時貼著兩個邊界時，往線的兩側各看一點點：有一側是房內空地（例如柱子埋在牆角、柱面就是房間的邊）就能走；
      // 兩側都不是空地（柱子貼牆、兩根柱子相貼的 0 寬縫）才擋住
      if ((inRoom === 0) + inObs.filter((v) => v === 0).length >= 2) {
        const L2 = Math.hypot(b[0] - a[0], b[1] - a[1]), nx = -(b[1] - a[1]) / L2 * SIDE, ny = (b[0] - a[0]) / L2 * SIDE;
        const free = (q) => inPoly(q, room) > 0 && !obstacles.some((o) => inPoly(q, o) >= 0);
        if (!free([mp[0] + nx, mp[1] + ny]) && !free([mp[0] - nx, mp[1] - ny])) return false;
      }
    }
    return true;
  }

  function segmentDistance(a, b, c, d) {
    if (segCross(a, b, c, d)) return 0;
    return Math.min(dist(a, closestOnSeg(a, c, d)), dist(b, closestOnSeg(b, c, d)),
      dist(c, closestOnSeg(c, a, b)), dist(d, closestOnSeg(d, a, b)));
  }

  function awayFrom(a, b, polygons) {
    return polygons.every((poly) => poly.every((p, i) => segmentDistance(a, b, p, poly[(i + 1) % poly.length]) >= CLEARANCE - EPS));
  }

  function clearPoint(p, room, obstacles) {
    return inPoly(p, room) > 0 && !obstacles.some((o) => inPoly(p, o) >= 0) && awayFrom(p, p, [room, ...obstacles]);
  }

  // 每一整段都檢查最小距離，不能只檢查端點，否則斜線仍會切到牆角／柱角。
  function canWalk(a, b, room, obstacles) {
    return clearPoint(a, room, obstacles) && clearPoint(b, room, obstacles) &&
      inFreeSpace(a, b, room, obstacles) && awayFrom(a, b, [room, ...obstacles]);
  }

  // 內縮外框、外擴障礙物提供轉彎候選。凹多邊形內縮可能自交或消失，
  // 所以它們只提供候選；每個點和線段仍回到原始幾何驗證 CLEARANCE，絕不靠錯誤外框放行。
  // 轉角採平移邊的交點（保守的折線），不以圓弧近似最短路徑。
  function routeRegion(room, obstacles) {
    const contours = [insetPolygon(room, CLEARANCE), ...obstacles.map((o) => insetPolygon(o, -CLEARANCE))];
    const nodes = contours.flat();
    contours.forEach((p, i) => contours.slice(i + 1).forEach((q) => p.forEach((a, j) => q.forEach((b, k) => {
      const x = segCross(a, p[(j + 1) % p.length], b, q[(k + 1) % q.length]);
      if (x) nodes.push(x);
    }))));
    return { contours, nodes: nodes.filter((p, i) => clearPoint(p, room, obstacles) && nodes.findIndex((q) => dist(p, q) < EPS) === i) };
  }

  // 門／樓梯可以在牆面上。只豁免「端點垂直接入可行範圍」的短段與其宿主邊，
  // 其餘牆面與所有障礙物仍保留淨距；一般路線不得藉門口貼牆抄近路。
  function entryAnchors(p, room, obstacles) {
    if (clearPoint(p, room, obstacles)) return [p];
    const area = room.reduce((s, a, i) => { const b = room[(i + 1) % room.length]; return s + a[0] * b[1] - b[0] * a[1]; }, 0);
    const sides = room.map((a, i) => {
      const b = room[(i + 1) % room.length], q = closestOnSeg(p, a, b), L = dist(a, b), sign = area > 0 ? 1 : -1;
      return { a, b, q, L, i, distance: dist(p, q), n: [-(b[1] - a[1]) / L * sign, (b[0] - a[0]) / L * sign] };
    }).filter((s) => s.L > EPS);
    const nearest = Math.min(...sides.map((s) => s.distance));
    if (inPoly(p, room) < 0 && nearest > DOOR_WALL_MAX) return [];
    return sides.filter((s) => s.distance <= nearest + EPS).map((s) => {
      const anchor = [s.q[0] + s.n[0] * CLEARANCE, s.q[1] + s.n[1] * CLEARANCE];
      if (!clearPoint(anchor, room, obstacles) || !inFreeSpace(s.q, anchor, room, obstacles) || !awayFrom(p, anchor, obstacles)) return null;
      // Revit 同一面牆的相鄰邊段可能有微小角度差。只將連續、近共線的邊段併入宿主，
      // 不把遠處平行牆或真正轉角當成門洞；anchor 與後續整段路線仍須完整保留 CLEARANCE。
      const sameWall = (t) => Math.abs((t.b[0] - t.a[0]) * s.n[0] + (t.b[1] - t.a[1]) * s.n[1]) <= HOST_ANGLE * t.L &&
        (t.b[0] - t.a[0]) * (s.b[0] - s.a[0]) + (t.b[1] - t.a[1]) * (s.b[1] - s.a[1]) > 0 &&
        [t.a, t.b].every((v) => dist(v, closestOnLine(v, s.a, s.b)) <= HOST_OFFSET);
      const host = new Set([s]);
      for (const direction of [-1, 1]) for (let j = 1; j < sides.length; j++) {
        const t = sides[(sides.indexOf(s) + direction * j + sides.length) % sides.length];
        if (!sameWall(t)) break;
        host.add(t);
      }
      if (sides.some((t) => !host.has(t) && segmentDistance(p, anchor, t.a, t.b) < CLEARANCE - EPS)) return null;
      return anchor;
    }).filter(Boolean);
  }

  function roomGraph(room, obstacles, entries) {
    const region = routeRegion(room, obstacles);
    const anchors = entries.map((p) => entryAnchors(p, room, obstacles));
    const nodes = [...region.nodes], add = (p) => {
      let i = nodes.findIndex((q) => dist(p, q) < EPS);
      if (i < 0) { i = nodes.length; nodes.push(p); }
      return i;
    };
    const anchorIds = anchors.map((ps) => ps.map(add));
    const adj = visibility(nodes, room, obstacles);
    const entryIds = entries.map((p, i) => {
      const id = add(p); adj[id] = adj[id] || [];
      for (const a of anchorIds[i]) if (a !== id) {
        const w = dist(p, nodes[a]); adj[id].push([a, w]); adj[a].push([id, w]);
      }
      return id;
    });
    return { nodes, adj, entryIds, region };
  }

  // 繞障礙物時可以轉彎的點：落在空間裡的障礙物角點，加上障礙物邊和空間外框的交點。
  // 柱子常一半埋在牆裡，角點在空間外；只用角點的話，貼牆的柱子旁邊沒有轉彎點，路線會繞遠路
  function turnPoints(poly, obstacles) {
    const out = obstacles.flat().filter((p) => inPoly(p, poly) >= 0);
    for (const o of obstacles) o.forEach((a, i) => {
      const b = o[(i + 1) % o.length];
      poly.forEach((c, k) => { const x = segCross(a, b, c, poly[(k + 1) % poly.length]); if (x) out.push(x); });
    });
    return out;
  }

  // 可見性圖：任兩點之間能直接走就連一條邊
  function visibility(nodes, room, obstacles) {
    const adj = nodes.map(() => []);
    for (let i = 0; i < nodes.length; i++) for (let j = i + 1; j < nodes.length; j++) {
      if (!canWalk(nodes[i], nodes[j], room, obstacles)) continue;
      const w = dist(nodes[i], nodes[j]);
      adj[i].push([j, w]); adj[j].push([i, w]);
    }
    return adj;
  }

  // 房內路徑只在已內縮的路線範圍行走；門口透過受限的垂直接入段連接。
  function indoorPath(from, door, room, obstacles) {
    if (!clearPoint(from, room, obstacles)) return null;
    const graph = roomGraph(room, obstacles, [door, from]);
    return netPath(graph, graph.entryIds[1], graph.entryIds[0]);
  }

  // 最遠點：在同一份「路線可行範圍」上找走到樓梯距離最大的候選。
  // exitCost[i] 是從第 i 扇門出去、走到最近樓梯的走廊段長度
  // 保留邊界取樣與局部搜尋，並非任意平面最遠點的精確證明。
  function farthestPoint(room, obstacles, doorPts, exitCost) {
    const graph = roomGraph(room, obstacles, doorPts), { nodes } = graph;
    const init = nodes.map(() => Infinity);
    graph.entryIds.forEach((id, i) => { init[id] = Math.min(init[id], exitCost[i]); });
    const D = distAll(graph.adj, init);
    const ok = (p) => clearPoint(p, room, obstacles);
    const f = (p) => nodes.reduce((m, v, i) => (D[i] < Infinity && canWalk(p, v, room, obstacles) ? Math.min(m, dist(p, v) + D[i]) : m), Infinity);

    // 先取角點（照外框順序），再沿每條邊每 0.25 m 取點
    const samples = graph.region.nodes.map((p) => ({ p, v: f(p) }));
    const edges = graph.region.contours.flatMap((poly) => poly.map((a, i) => {
      const b = poly[(i + 1) % poly.length], k = Math.max(1, Math.ceil(dist(a, b) / STEP));
      const at = (t) => [a[0] + t * (b[0] - a[0]), a[1] + t * (b[1] - a[1])];
      const pts = [];
      for (let j = 0; j <= k; j++) { const p = at(j / k); pts.push(ok(p) ? { p, t: j / k, v: f(p) } : null); }
      return { at, k, pts };
    }));
    edges.forEach((e) => e.pts.forEach((s, j) => { if (s && j > 0 && j < e.k) samples.push(s); }));
    if (!samples.length) return null;
    if (samples.some((s) => s.v === Infinity)) return { p: samples.find((s) => s.v === Infinity).p, v: Infinity };
    let best = samples.reduce((a, b) => (b.v > a.v + EPS ? b : a));

    // 細找：距離沿著邊每走 1 m 最多變 1 m，所以區間 [s1, s2] 內的最大值不超過 (v1 + v2 + 長度) / 2
    // 這個上限比目前最遠還大的區間才細找（三分法）
    for (const e of edges) for (let j = 1; j <= e.k; j++) {
      const s1 = e.pts[j - 1], s2 = e.pts[j];
      if (!s1 || !s2 || !canWalk(s1.p, s2.p, room, obstacles) || (s1.v + s2.v + dist(s1.p, s2.p)) / 2 <= best.v + EPS) continue;
      let lo = s1.t, hi = s2.t;
      for (let it = 0; it < 40; it++) {
        const m1 = lo + (hi - lo) / 3, m2 = hi - (hi - lo) / 3;
        if (f(e.at(m1)) < f(e.at(m2))) lo = m1; else hi = m2;
      }
      const p = e.at((lo + hi) / 2), v = f(p);
      if (ok(p) && v > best.v + 1e-4) best = { p, v };
    }
    return best;
  }

  // 可行路徑：房內路徑（最遠點 → 門）→ 垂直接到走廊中心線 → 沿走廊網路 → 接回樓梯計算終點；走不到回傳 null
  // doorNode、stairNode 是門和樓梯在 net 上的接點編號
  function routeTo(inside, stair, net, doorNode, stairNode) {
    const mid = inside && doorNode != null && stairNode != null && netPath(net, doorNode, stairNode);
    return mid && simplify([...inside, ...mid, stair.entry_point]);
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

  // 點落在哪段走廊中心線上（找不到回傳 undefined）
  function corridorAt(corridors, p) {
    return corridors.find((c) => c.centerline.slice(1).some((q, i) => dist(p, closestOnSeg(p, c.centerline[i], q)) < SNAP));
  }

  // 一間房的量測結果；資料不足時回傳 { missing: "原因" }
  // 多扇門：每個候選點先找「任一扇門 × 任一座樓梯」最短的路線，再取這條最短路線最長的點當最遠點
  // 程式出錯也不讓整個檢討當掉，改回報成這間房「模型資料不足」
  function measureRoom(room, snapshot) {
    try {
      return measure(room, snapshot);
    } catch (e) {
      return { missing: `幾何計算出錯（${e && e.message}），請把這間房的資料回報給 B` };
    }
  }

  const pt = (p) => `(${r2(p[0])}, ${r2(p[1])})`;
  const isPt = (p) => Array.isArray(p) && p.length >= 2 && Number.isFinite(p[0]) && Number.isFinite(p[1]);
  const label = (x) => x.name || x.number || x.element_id;

  // 網路上和 from 連在一起的所有節點
  function component(net, from) {
    const seen = new Set([from]), queue = [from];
    while (queue.length) for (const [v] of net.adj[queue.shift()] || []) if (!seen.has(v)) { seen.add(v); queue.push(v); }
    return seen;
  }

  // 走不到樓梯時，找出門這一側和樓梯那一側最接近的兩點，就是最可能斷開的地方
  function gapReport(net, corridors, door, doorNode, stairNodes) {
    if (doorNode == null) return `門 ${label(door)} 沒有接到走廊中心線或通行空間`;
    const A = component(net, doorNode), B = new Set();
    for (const t of stairNodes) if (t != null && !A.has(t)) component(net, t).forEach((i) => B.add(i));
    let gap = null;
    for (const a of A) for (const b of B) {
      const d = dist(net.nodes[a], net.nodes[b]);
      if (!gap || d < gap.d) gap = { a: net.nodes[a], b: net.nodes[b], d };
    }
    if (!gap) return `門 ${label(door)} 接上的通道和所有直通樓梯都沒有連起來（需保留 ${CLEARANCE} m 路線淨距，中心線走廊需有至少 ${r2(2 * CLEARANCE)} m 的已知淨寬）`;
    const at = (p) => { const c = corridorAt(corridors, p); return (c ? c.name + " " : "") + pt(p); };
    return `門 ${label(door)} 走不到樓梯：可行範圍斷口在 ${at(gap.a)} 和 ${at(gap.b)} 之間，相距 ${r2(gap.d)} m；請確認 ${CLEARANCE} m 路線淨距、門口接入點與走廊淨寬`;
  }

  // 房間外框和通行空間外框重疊（共線、相距 5 cm 內）且長 0.3 m 以上的邊段 → 開口，位置取重疊段中點
  // 0.3 m 只是排除碰到角的小段，是自訂假設，不是法規數字
  const OPENING_MIN = 0.3;
  function openings(room, rooms) {
    const out = [], poly = room.boundary;
    for (const sp of rooms) {
      if (sp.element_id === room.element_id || sp.is_circulation !== true || !sp.boundary_closed || !Array.isArray(sp.boundary) || sp.boundary.length < 3) continue;
      poly.forEach((a, i) => {
        const b = poly[(i + 1) % poly.length], L = dist(a, b);
        if (L < OPENING_MIN) return;
        const t = (p) => ((p[0] - a[0]) * (b[0] - a[0]) + (p[1] - a[1]) * (b[1] - a[1])) / (L * L);
        sp.boundary.forEach((c, j) => {
          const d = sp.boundary[(j + 1) % sp.boundary.length];
          if (dist(c, closestOnLine(c, a, b)) > SNAP || dist(d, closestOnLine(d, a, b)) > SNAP) return;
          const lo = Math.max(0, Math.min(t(c), t(d))), hi = Math.min(1, Math.max(t(c), t(d)));
          if ((hi - lo) * L < OPENING_MIN) return;
          const m = (lo + hi) / 2;
          out.push({ element_id: `opening:${room.element_id}-${sp.element_id}-${out.length}`, position: [a[0] + (b[0] - a[0]) * m, a[1] + (b[1] - a[1]) * m],
            room_ids: [room.element_id, sp.element_id], is_opening: true, space_name: label(sp), width_m: (hi - lo) * L });
        });
      });
    }
    return out;
  }
  // 點 p 在直線 ab（不限線段）上最近的點
  function closestOnLine(p, a, b) {
    const L2 = (b[0] - a[0]) ** 2 + (b[1] - a[1]) ** 2, k = ((p[0] - a[0]) * (b[0] - a[0]) + (p[1] - a[1]) * (b[1] - a[1])) / L2;
    return [a[0] + (b[0] - a[0]) * k, a[1] + (b[1] - a[1]) * k];
  }

  function measure(room, snapshot) {
    const notes = [];
    if (!room.boundary_closed) return { missing: "房間邊界未封閉，無法決定居室範圍" };
    if (!Array.isArray(room.boundary) || room.boundary.filter(isPt).length < 3) return { missing: "房間外框少於 3 個有效的點" };
    const doors = [];
    for (const id of room.door_ids || []) {
      const d = (snapshot.doors || []).find((x) => x.element_id === id);
      if (!d) notes.push(`門 ${id} 在快照裡找不到，已略過`);
      else if (!isPt(d.position)) notes.push(`門 ${id} 沒有座標，已略過`);
      else doors.push(d);
    }
    // 沒有門、但和通行空間共用一段外框（Revit 用房間分隔線分開、中間沒有牆）→ 當成開口
    for (const o of openings(room, snapshot.rooms || [])) {
      doors.push(o);
      notes.push(`和 ${o.space_name} 共用 ${r2(o.width_m)} m 的邊（沒有門的開口），取中點當出入口`);
    }
    if (!doors.length) return { missing: room.door_ids && room.door_ids.length ? `找不到房間的門（${notes.join("；")}）` : "房間沒有門（door_ids 是空的），也沒有和通行空間相通的開口" };
    const stairs = [];
    // is_direct_stair 是 false 才排除；null（未確認）照樣算距離，結果由 C 標需人工確認
    for (const st of snapshot.stairs || []) {
      if (st.is_direct_stair === false) notes.push(`${label(st)} 已標示不是直通樓梯，沒有列入候選`);
      else if (!isPt(st.entry_point)) notes.push(`${label(st)} 沒有計算終點座標，沒有列入候選`);
      else stairs.push(st);
    }
    if (!stairs.length) {
      const all = snapshot.stairs || [];
      return { missing: !all.length ? "快照裡沒有樓梯（終點未定的樓梯在 unresolved_stairs，不列入計算）" : all.every((st) => st.is_direct_stair === false) ? "樓梯都已標示不是直通樓梯" : "樓梯都沒有計算終點座標" };
    }

    // 柱子等固定障礙物：snapshot.obstacles[].footprint，沒有這個欄位就當房內沒有障礙物
    const obstacles = (snapshot.obstacles || []).map((o) => o.footprint).filter((f) => Array.isArray(f) && f.length >= 3);
    const corridors = (snapshot.corridors || []).filter((c) => Array.isArray(c.centerline) && c.centerline.length >= 2);
    const spaces = walkableSpaces(room, snapshot);
    if (!corridors.length && !spaces.length) return { missing: "沒有走廊中心線或通行空間，無法排路線" };
    // 這間房的門排前面；其他門只有連接兩個可走空間的才要接進來（例如客廳通走廊的門）
    const spaceIds = new Set(spaces.map((s) => s.element_id));
    const passDoors = (snapshot.doors || []).filter((d) => isPt(d.position) && !doors.includes(d) && (d.room_ids || []).filter((id) => spaceIds.has(id)).length >= 2);
    const net = walkNetwork(corridors, spaces, [...doors, ...passDoors], stairs, obstacles);

    // 直接以真實門座標建圖；宿主牆內的短接入也由同一個淨距檢查處理，不能事後盲目補線。
    const gates = doors.map((d) => d.position);
    const exitCost = doors.map((d, di) => Math.min(...stairs.map((s, si) => {
      const path = routeTo([d.position], s, net, net.doorNode[di], net.stairNode[si]);
      return path ? plen(path) : Infinity;
    })));
    if (exitCost.every((c) => c === Infinity))
      return { missing: doors.map((d, di) => gapReport(net, corridors, d, net.doorNode[di], net.stairNode)).join("；") };
    doors.forEach((d, di) => { if (exitCost[di] === Infinity) notes.push(gapReport(net, corridors, d, net.doorNode[di], net.stairNode) + "，這扇門不列入計算"); });
    const fp = farthestPoint(room.boundary, obstacles, gates, exitCost);
    if (!fp) return { missing: `房間內沒有保留 ${CLEARANCE} m 路線淨距的候選位置；請檢查房間邊界、通道寬度與障礙物` };
    if (fp.v === Infinity) return { missing: `房內 ${pt(fp.p)} 附近在保留 ${CLEARANCE} m 路線淨距後走不到任何一扇門；請檢查狹窄通道或障礙物` };

    const far = fp.p, pi = 0;
    const insides = [doors.map((d, di) => {
      return indoorPath(far, gates[di], room.boundary, obstacles);
    })];
    const route = (pi, di, si) => {
      const path = routeTo(insides[pi][di], stairs[si], net, net.doorNode[di], net.stairNode[si]);
      return { p: far, inside: insides[pi][di], door: doors[di], s: stairs[si], path, d: path ? plen(path) : Infinity };
    };
    const shortest = (routes) => routes.reduce((a, b) => (b.d < a.d ? b : a));
    const best = shortest(doors.flatMap((_, di) => stairs.map((_, si) => route(pi, di, si))));
    const candidates = stairs.map((s, si) => {
      const r = shortest(doors.map((_, di) => route(pi, di, si)));
      if (!r.path) notes.push(`從這間房走不到 ${label(s)}`);
      return { stair_id: s.element_id, stair_name: s.name, door_id: r.path ? r.door.element_id : null, distance_m: r.path ? r2(r.d) : null, path: r.path };
    });
    const indoor = plen(best.inside);
    // 路線穿過別的居室時寫出來，讓人工覆核看得到
    const mid = netPath(net, net.doorNode[doors.indexOf(best.door)], net.stairNode[stairs.indexOf(best.s)]) || [];
    const through = spaces.filter((s) => s.is_circulation !== true && passDoors.concat(doors).some((d) => (d.room_ids || []).includes(s.element_id) && mid.some((p) => dist(p, d.position) < SNAP)));
    if (through.length) notes.push(`路線穿過 ${through.map(label).join("、")}（居室），請確認這條路線實際可通行`);
    if (best.s.is_direct_stair !== true) notes.push(`算到的 ${label(best.s)} 還沒確認是直通樓梯`);
    if (String(best.s.endpoint_source || "").startsWith("auto:")) notes.push(`${label(best.s)} 的第一階位置是外掛從梯段路徑自動推的`);
    notes.push(`路線以 ${CLEARANCE} m 牆面／固定障礙物淨距計算；門口與樓梯終點僅保留垂直接入短段。這是專案幾何假設，不是法定數字`);
    if (corridors.length) notes.push(`只有中心線的走廊依已提供的有效淨寬檢查，並非從實體牆面量得路線淨距；未知或小於 ${r2(2 * CLEARANCE)} m 的走廊不作路線`);
    return {
      geometry_version: GEOMETRY_VERSION, route_clearance_m: CLEARANCE,
      door_id: best.door.element_id,
      far_point: far, path: best.path, stair_id: best.s.element_id, stair_name: best.s.name,
      stair_confirmed: best.s.is_direct_stair === true, endpoint_source: best.s.endpoint_source || null,
      endpoint_type: best.s.endpoint_type, indoor_m: r2(indoor), corridor_m: r2(best.d - indoor), distance_m: r2(best.d), distance_exact_m: best.d,
      turns: turns(best.path), candidates, notes,
    };
  }

  const api = { measureRoom, insetPolygon, insetCorners, farthestPoint, farPoint, corridorNetwork, walkNetwork, netPath, inPoly, canWalk, indoorPath, routeTo, turns, corridorAt, dist, plen, r2 };
  if (typeof module !== "undefined") module.exports = api; else root.EgressGeometry = api;
})(this);
