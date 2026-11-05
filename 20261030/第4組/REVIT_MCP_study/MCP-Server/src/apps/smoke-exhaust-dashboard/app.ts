/**
 * Deterministic smoke exhaust compliance dashboard — MCP App.
 *
 * The app is launched by get_all_levels, then calls only:
 * get_all_levels, get_rooms_by_level, and check_smoke_exhaust_windows
 * (always with colorize=false). Scenario values are computed by the C# check command.
 */
import { App } from "@modelcontextprotocol/ext-apps";
import * as THREE from "three";
import { OrbitControls } from "three/addons/controls/OrbitControls.js";

const app = new App({ name: "smoke-exhaust-dashboard", version: "1.0.0" }, {});
const root = document.getElementById("root") as HTMLElement;
const WALL_THICKNESS_MM = 200;

type Dict = Record<string, unknown>;
type LevelRow = { ElementId: unknown; Name: string; Elevation?: unknown };
type RoomRow = { ElementId: unknown; Name: string; Number: string; Area: number };
type WindowRow = Dict;
type CheckedRoom = Dict & { RoomId: unknown; Windows?: WindowRow[]; Summary?: Dict };
const ALLOWED_TOOLS = new Set([
  "get_all_levels",
  "get_rooms_by_level",
  "check_smoke_exhaust_windows",
  "get_room_info",
  "plan_smoke_exhaust_window_plan",
  "apply_smoke_exhaust_window_plan",
  "undo_smoke_exhaust_window_plan",
  "plan_smoke_exhaust_window_retrofit",
  "apply_smoke_exhaust_window_retrofit",
  "undo_smoke_exhaust_window_retrofit",
  "plan_smoke_exhaust_window_replacement",
  "apply_smoke_exhaust_window_replacement",
  "undo_smoke_exhaust_window_replacement",
  "select_element",
  "zoom_to_element",
]);

let levels: LevelRow[] = [];
let rooms: RoomRow[] = [];
let checkedRooms = new Map<string, CheckedRoom>();
let selectedRoom: RoomRow | null = null;
let selectedWindow: WindowRow | null = null;
let selectedLevel = "";
let busy = false;
let errorText = "";
let locatingWindowId: number | null = null;
let locationStatus: { windowId: number; success: boolean; technicalMessage?: string } | null = null;
type PlannedPlacement = {
  hostWallId: number; locationX: number; locationY: number; sillHeightMm: number; widthMm: number; heightMm: number;
  isDiagnostic?: boolean; conflictReasons?: string[];
};
type WindowPlan = {
  planId: string; roomId: string; windowType: string; placements: PlannedPlacement[]; diagnosticPlacements: PlannedPlacement[];
  requestedCount: number; foundPositions: number; placementSufficient: boolean; areaEstimateResult: string;
  predictedResult: Dict; canApply: boolean;
};
let windowPlanPreview: WindowPlan | null = null;
type WindowRetrofitPlan = {
  planId: string; roomId: string; windowType: string; originalWindows: Dict[]; proposedWindows: Dict[];
  currentTotalEffectiveArea: number; proposedTotalEffectiveArea: number; proposedTotalEffectiveAreaExact: number;
  requiredArea: number; deficit: number; predictedResult: string; ceilingHeightMm: number; headWithinCeiling: boolean; canApply: boolean;
};
let windowRetrofitPreview: WindowRetrofitPlan | null = null;
let windowPlanInsufficient = false;
let retrofitOperationId: string | null = null;
let retrofitOperationRoomId: string | null = null;
type WindowReplacementPlan = { planId: string; roomId: string; selectedIds: string[]; targetType: string; sources: Dict[]; proposed: Dict[]; total: number; current: number; required: number; deficit: number; result: string; safe: boolean; warnings: string[]; blockingWarnings: string[]; informationalWarnings: string[]; canApply: boolean };
let windowReplacementPreview: WindowReplacementPlan | null = null;
let replacementOperationId: string | null = null;
let replacementOperationRoomId: string | null = null;
let windowPlanBusy = false;
let windowPlanError = "";
let windowPlanMessage = "";
let windowOperationId: string | null = null;
let windowOperationRoomId: string | null = null;
type RoomBoundarySegment = { StartX: number; StartY: number; EndX: number; EndY: number; Length?: number; WallId?: unknown; HostWallId?: unknown; ElementId?: unknown };
type WallLocationRow = Dict & { ElementId: unknown; LocationLine?: { StartX: number; StartY: number; EndX: number; EndY: number }; ClipStartX?: number; ClipStartY?: number; ClipEndX?: number; ClipEndY?: number };
type RoomBounds = { minX: number; maxX: number; minY: number; maxY: number; centerX: number; centerY: number };
type RoomViewer = {
  renderer: THREE.WebGLRenderer; scene: THREE.Scene; camera: THREE.PerspectiveCamera; controls: OrbitControls;
  roomGroup: THREE.Group; canvasHost: HTMLElement; status: HTMLElement; resizeObserver: ResizeObserver | null;
  animationFrame: number; roomId: string; requestId: number;
  raycaster: THREE.Raycaster; pointer: THREE.Vector2; windowMeshes: Map<string, THREE.Mesh>;
  selectedOutlines: Map<string, THREE.LineSegments>; boundarySegments: RoomBoundarySegment[];
  previewObjects: THREE.Object3D[]; fadedWindowIds: Set<string>;
  originX: number; originY: number; clickHandler: (event: MouseEvent) => void;
};
let roomViewer: RoomViewer | null = null;

function el<K extends keyof HTMLElementTagNameMap>(
  tag: K,
  className = "",
  text = ""
): HTMLElementTagNameMap[K] {
  const node = document.createElement(tag);
  if (className) node.className = className;
  if (text) node.textContent = text;
  return node;
}

function parseResult(result: unknown): unknown {
  const r = result as {
    content?: Array<{ type?: string; text?: string }>;
    structuredContent?: unknown;
    isError?: boolean;
  } | null;
  if (r?.isError) {
    const message = r.content?.find((c) => c.type === "text")?.text ?? "MCP 工具呼叫失敗";
    throw new Error(message);
  }
  if (r?.structuredContent != null) return r.structuredContent;
  const textBlock = r?.content?.find((c) => c.type === "text" && typeof c.text === "string");
  if (textBlock?.text) {
    try {
      return JSON.parse(textBlock.text);
    } catch {
      throw new Error(textBlock.text);
    }
  }
  return result ?? null;
}

function object(value: unknown): Dict {
  return value && typeof value === "object" ? value as Dict : {};
}

function arrayAt(value: unknown, key: string): Dict[] {
  const rows = object(value)[key];
  return Array.isArray(rows) ? rows.filter((row) => row && typeof row === "object") as Dict[] : [];
}

async function callAllowedTool(name: string, args: Dict): Promise<unknown> {
  if (!ALLOWED_TOOLS.has(name)) throw new Error(`Dashboard blocked tool: ${name}`);
  if (window.parent === window) {
    const response = await fetch("/api/tools/call", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name, arguments: args }),
    });
    const result = await response.json();
    if (!response.ok) throw new Error(typeof result.error === "string" ? result.error : `HTTP ${response.status}`);
    return parseResult(result);
  }
  return parseResult(await app.callServerTool({ name, arguments: args }));
}

function textValue(value: unknown, fallback = "—"): string {
  return value == null || value === "" ? fallback : String(value);
}

function numberValue(value: unknown): number | null {
  return typeof value === "number" && Number.isFinite(value) ? value : null;
}

function validWindowId(value: unknown): number | null {
  if (value == null || (typeof value === "string" && value.trim() === "")) return null;
  let id: number;
  try { id = Number(value); } catch { return null; }
  return Number.isFinite(id) && id > 0 ? id : null;
}

function fmt(value: unknown, digits = 4): string {
  const n = numberValue(value);
  return n == null ? "—" : n.toLocaleString("zh-TW", { maximumFractionDigits: digits });
}

function idKey(value: unknown): string {
  return value == null ? "" : String(value);
}

function diagnosis(win: WindowRow): string {
  if (win.NeedsManualConfirm === true) return "窗型需人工確認";
  const zoneHeight = numberValue(win.HeightInZone);
  const ratio = numberValue(win.OpeningRatio);
  if (zoneHeight == null || ratio == null) return "資料不足，需人工確認";
  if (zoneHeight <= 0) return "未進入天花板下 800 mm 有效帶";
  if (zoneHeight > 0 && ratio === 0) return "位於有效帶內，但無法開啟";
  if (zoneHeight > 0 && ratio > 0) return "可貢獻有效開口";
  return "資料不足，需人工確認";
}

function statusClass(result: string): string {
  if (result === "PASS") return "badge pass";
  if (result === "FAIL") return "badge fail";
  return "badge unevaluated";
}

function diagnosisClass(value: string): string {
  if (value === "可貢獻有效開口") return "diagnosis-badge contributing";
  if (value === "位於有效帶內，但無法開啟" || value === "窗型需人工確認") return "diagnosis-badge warning";
  return "diagnosis-badge inactive";
}

function fmtRatio(value: unknown): string {
  const n = numberValue(value);
  return n == null ? "—" : `${Math.round(n * 100)}%`;
}

function render(): void {
  root.replaceChildren();
  const header = el("header", "header");
  header.append(el("h1", "", "自然排煙開口檢核助手"));
  header.append(el("p", "subtitle", "選擇樓層、房間與窗戶，查看有效開口檢核結果及假設改善情境。資料來自 Revit 專案。"));
  root.append(header);

  const levelSection = el("section", "panel level-panel");
  levelSection.append(el("label", "field-label", "選擇樓層"));
  const select = el("select", "level-select") as HTMLSelectElement;
  select.setAttribute("aria-label", "選擇樓層");
  const placeholder = el("option", "", levels.length ? "請選擇樓層" : "正在載入樓層…") as HTMLOptionElement;
  placeholder.value = "";
  select.append(placeholder);
  for (const level of levels) {
    const option = el("option", "", `${level.Name}${numberValue(level.Elevation) == null ? "" : ` · ${fmt(level.Elevation, 2)} mm`}`) as HTMLOptionElement;
    option.value = level.Name;
    option.selected = level.Name === selectedLevel;
    select.append(option);
  }
  select.disabled = busy || levels.length === 0;
  select.addEventListener("change", () => { void loadLevel(select.value); });
  levelSection.append(select);
  if (busy) levelSection.append(el("span", "inline-status", "正在讀取房間與排煙檢核結果…"));
  if (errorText) levelSection.append(el("p", "error-message", errorText));
  root.append(levelSection);

  if (!selectedLevel || !rooms.length) {
    disposeRoomViewer();
    root.append(el("div", "empty-state", selectedLevel ? "此樓層沒有可列出的房間。" : "請先選擇樓層。"));
    return;
  }

  const roomSection = el("section", "panel");
  roomSection.append(el("div", "section-heading", `房間檢核摘要 · ${selectedLevel}`));
  const roomList = el("div", "room-list");
  for (const room of rooms) {
    const checked = checkedRooms.get(idKey(room.ElementId));
    const result = typeof checked?.Summary?.Result === "string" ? checked.Summary.Result : "本次未檢核";
    const button = el("button", `room-row${selectedRoom && idKey(selectedRoom.ElementId) === idKey(room.ElementId) ? " selected" : ""}`);
    button.type = "button";
    button.disabled = !checked || busy || windowPlanBusy;
    button.setAttribute("aria-label", `房間 ${room.Number} ${room.Name} ${result}`);
    const identity = el("span", "room-identity", `房間 ${textValue(room.Number)} · ${textValue(room.Name)}`);
    const area = el("span", "room-area", `${fmt(room.Area, 2)} m²`);
    const badge = el("span", statusClass(result), result);
    button.append(identity, area, badge);
    button.addEventListener("click", () => {
      if (!checked) return;
      if (windowPlanPreview && windowPlanPreview.roomId !== idKey(room.ElementId)) {
        windowPlanPreview = null; clearWindowPlanPreviewMeshes();
      }
      if (windowRetrofitPreview && windowRetrofitPreview.roomId !== idKey(room.ElementId)) {
        windowRetrofitPreview = null; clearWindowPlanPreviewMeshes();
      }
      if (windowReplacementPreview && windowReplacementPreview.roomId !== idKey(room.ElementId)) {
        windowReplacementPreview = null; clearWindowPlanPreviewMeshes();
      }
      if (windowPlanInsufficient && selectedRoom && idKey(selectedRoom.ElementId) !== idKey(room.ElementId)) windowPlanInsufficient = false;
      selectedRoom = room;
      selectedWindow = null;
      render();
    });
    roomList.append(button);
  }
  roomSection.append(roomList);
  root.append(roomSection);

  if (!selectedRoom) {
    disposeRoomViewer();
    root.append(el("div", "empty-state", "請點選已檢核的房間，查看房間摘要與單窗診斷。"));
    return;
  }
  const checked = checkedRooms.get(idKey(selectedRoom.ElementId));
  if (!checked) return;
  renderRoomDetails(checked);
}

function renderRoomDetails(room: CheckedRoom): void {
  const summary = object(room.Summary);
  const summaryPanel = el("section", "panel room-summary");
  summaryPanel.append(el("div", "section-heading", `房間 ${textValue(room.RoomNumber)} · ${textValue(room.RoomName)}`));
  const metrics = el("div", "metrics");
  const result = textValue(summary.Result, "本次未檢核");
  const metricRows: Array<[string, string, string]> = [
    ["房間面積", `${fmt(room.RoomArea, 2)} m²`, ""],
    ["需求有效開口面積", `${fmt(summary.RequiredArea)} m²`, ""],
    ["目前有效開口面積", `${fmt(summary.TotalEffectiveArea)} m²`, ""],
    ["不足面積", `${fmt(summary.Deficit)} m²`, ""],
    ["房間檢核結果", result, statusClass(result)],
  ];
  for (const [label, value, cls] of metricRows) {
    const metric = el("div", "metric");
    metric.append(el("span", "metric-label", label));
    metric.append(el("strong", cls ? `metric-value ${cls}` : "metric-value", value));
    metrics.append(metric);
  }
  summaryPanel.append(metrics);
  root.append(summaryPanel);

  renderWindowHeightDiagram(room);
  renderRoomViewer(room);
  renderRoomImprovementPlan(room);

  const windows = Array.isArray(room.Windows) ? room.Windows : [];
  const windowPanel = el("section", "panel");
  windowPanel.append(el("div", "section-heading", `單窗診斷表 · ${windows.length} 扇窗`));
  if (!windows.length) {
    windowPanel.append(el("div", "empty-state", "檢核結果沒有回傳窗戶明細。"));
  } else {
    const tableWrap = el("div", "table-wrap");
    const table = el("table", "data-table");
    const headings = ["窗戶 ID", "族群／類型", "有效帶內高度", "開啟比例", "有效開口面積", "診斷結果"];
    const thead = el("thead");
    const tr = el("tr");
    for (const heading of headings) tr.append(el("th", "", heading));
    thead.append(tr);
    table.append(thead);
    const tbody = el("tbody");
    for (const win of windows) {
      const row = el("tr", `window-row${selectedWindow && idKey(selectedWindow.WindowId) === idKey(win.WindowId) ? " selected" : ""}`);
      row.tabIndex = 0;
      row.setAttribute("role", "button");
      row.setAttribute("aria-label", `選取窗戶 ${textValue(win.WindowId)}`);
      const familyType = `${textValue(win.FamilyName)} / ${textValue(win.TypeName)}`;
      const values = [
        textValue(win.WindowId), familyType,
        `${fmt(win.HeightInZone, 1)} mm`, fmtRatio(win.OpeningRatio), `${fmt(win.EffectiveArea)} m²`, diagnosis(win),
      ];
      for (const [index, value] of values.entries()) {
        const cell = el("td");
        if (index === values.length - 1) cell.append(el("span", diagnosisClass(value), value));
        else cell.textContent = value;
        row.append(cell);
      }
      const selectWindow = () => { selectedWindow = win; render(); };
      row.addEventListener("click", selectWindow);
      row.addEventListener("keydown", (event) => {
        if (event.key === "Enter" || event.key === " ") {
          event.preventDefault();
          selectWindow();
        }
      });
      tbody.append(row);
    }
    table.append(tbody);
    tableWrap.append(table);
    windowPanel.append(tableWrap);
  }
  root.append(windowPanel);

  if (selectedWindow) renderWindowDetails(room, selectedWindow);
}

function renderRoomViewer(room: CheckedRoom): void {
  const panel = el("section", "panel viewer-3d-panel");
  panel.append(el("div", "section-heading", "3D 空間示意"));
  panel.append(el("p", "viewer-3d-subtitle", "依 Revit 空間與窗戶位置建立的簡化 3D 檢核視圖"));
  const host = el("div", "viewer-3d-canvas");
  const smokeZoneBottom = numberValue(room.SmokeZoneBottom);
  const smokeZoneTop = numberValue(room.SmokeZoneTop);
  if (smokeZoneBottom != null && smokeZoneTop != null && smokeZoneTop > smokeZoneBottom) {
    const zoneLabel = el("div", "viewer-3d-zone-label", `有效排煙帶　${(smokeZoneBottom / 1000).toFixed(2)}–${(smokeZoneTop / 1000).toFixed(2)} m`);
    host.append(zoneLabel);
  }
  const legend = el("div", "viewer-3d-legend");
  for (const [color, label] of [["#9aa3ad", "未進有效帶"], ["#e9b949", "固定窗／無法開啟"], ["#38b66a", "可貢獻有效開口"], ["#55a9ff", "目前選取"]]) {
    const item = el("span", "viewer-3d-legend-item");
    const swatch = el("i", "viewer-3d-swatch");
    swatch.style.backgroundColor = color;
    if (label === "目前選取") { swatch.style.backgroundColor = "transparent"; swatch.style.borderColor = color; }
    item.append(swatch, document.createTextNode(label)); legend.append(item);
  }
  const retrofitLegendItem = el("span", "viewer-3d-legend-item");
  const retrofitSwatch = el("i", "viewer-3d-swatch");
  retrofitSwatch.style.backgroundColor = "#c36cff";
  retrofitLegendItem.append(retrofitSwatch, document.createTextNode("改善窗預覽"));
  legend.append(retrofitLegendItem);
  const diagnosticLegendItem = el("span", "viewer-3d-legend-item");
  const diagnosticSwatch = el("i", "viewer-3d-swatch");
  diagnosticSwatch.style.backgroundColor = "#e5485d";
  diagnosticLegendItem.append(diagnosticSwatch, document.createTextNode("診斷預覽（配置衝突）"));
  legend.append(diagnosticLegendItem);
  panel.append(legend);
  host.setAttribute("aria-label", "可旋轉與縮放的 3D 房間視圖");
  const status = el("div", "viewer-3d-status", "正在載入房間邊界…");
  host.append(status);
  root.append(panel);
  panel.append(host);

  if (!roomViewer) {
    try {
      const renderer = new THREE.WebGLRenderer({ antialias: true, alpha: false });
      renderer.setPixelRatio(Math.min(window.devicePixelRatio || 1, 2));
      renderer.setClearColor(0x171b22, 1);
      renderer.domElement.className = "viewer-3d-canvas-element";
      const scene = new THREE.Scene();
      scene.background = new THREE.Color(0x171b22);
      const camera = new THREE.PerspectiveCamera(45, 1, 0.1, 2000);
      const controls = new OrbitControls(camera, renderer.domElement);
      controls.enableDamping = true;
      const roomGroup = new THREE.Group();
      scene.add(new THREE.GridHelper(20, 20, 0x5d6b7c, 0x39424e));
      scene.add(roomGroup);
      const viewer: RoomViewer = {
        renderer, scene, camera, controls, roomGroup, canvasHost: host, status,
        resizeObserver: typeof ResizeObserver === "undefined" ? null : new ResizeObserver(() => resizeRoomViewer(viewer)),
        animationFrame: 0, roomId: "", requestId: 0,
        raycaster: new THREE.Raycaster(), pointer: new THREE.Vector2(), windowMeshes: new Map(), selectedOutlines: new Map(), previewObjects: [], fadedWindowIds: new Set(),
        boundarySegments: [], originX: 0, originY: 0, clickHandler: () => {},
      };
      viewer.clickHandler = (event) => {
        const rect = renderer.domElement.getBoundingClientRect();
        viewer.pointer.set(((event.clientX - rect.left) / rect.width) * 2 - 1, -((event.clientY - rect.top) / rect.height) * 2 + 1);
        viewer.raycaster.setFromCamera(viewer.pointer, viewer.camera);
        const hit = viewer.raycaster.intersectObjects([...viewer.windowMeshes.values()], false)[0];
        const win = hit?.object.userData.window as WindowRow | undefined;
        if (win) { selectedWindow = win; render(); }
      };
      renderer.domElement.addEventListener("click", viewer.clickHandler);
      roomViewer = viewer;
      const animate = () => {
        viewer.animationFrame = window.requestAnimationFrame(animate);
        controls.update();
        renderer.render(scene, camera);
      };
      animate();
    } catch (error) {
      status.textContent = "目前環境無法啟用 WebGL。";
      console.warn("Unable to initialize the 3D room viewer", error);
      return;
    }
  }

  const viewer = roomViewer;
  viewer.canvasHost = host;
  viewer.status = status;
  host.append(viewer.renderer.domElement);
  viewer.resizeObserver?.disconnect();
  viewer.resizeObserver?.observe(host);
  resizeRoomViewer(viewer);
  syncRoomWindowSelection(viewer);

  const roomId = idKey(room.RoomId);
  if (viewer.roomId === roomId) return;
  viewer.roomId = roomId;
  const requestId = ++viewer.requestId;
  clearRoomMeshes(viewer.roomGroup);
  status.textContent = "正在載入房間邊界…";
  void callAllowedTool("get_room_info", { roomId: Number(room.RoomId) })
    .then(async (value) => {
      if (roomViewer !== viewer || viewer.requestId !== requestId || idKey(selectedRoom?.ElementId) !== roomId) return;
      const info = object(value);
      const segments = Array.isArray(info.BoundarySegments) ? info.BoundarySegments as RoomBoundarySegment[] : [];
      const ceilingHeight = numberValue(room.CeilingHeight);
      if (!segments.length || ceilingHeight == null || ceilingHeight <= 0) {
        status.textContent = "沒有可用的房間邊界或天花高度資料。";
        return;
      }
      viewer.boundarySegments = segments;
      const outerLoop = getOuterBoundaryLoop(segments);
      if (!outerLoop || !buildRoomShell(viewer, outerLoop.segments, ceilingHeight)) {
        status.textContent = "房間邊界資料不足，無法建立 3D 外殼。";
        return;
      }
      addSmokeZone(viewer, outerLoop.segments, room);
      addRoomWindows(viewer, room);
      status.textContent = "";
    })
    .catch((error: unknown) => {
      if (roomViewer !== viewer || viewer.requestId !== requestId) return;
      status.textContent = "無法載入房間邊界，3D Viewer 仍可操作。";
      console.warn("Unable to load room boundary", error);
    });
}

function resizeRoomViewer(viewer: RoomViewer): void {
  const width = viewer.canvasHost.clientWidth;
  const height = viewer.canvasHost.clientHeight;
  if (width <= 0 || height <= 0) return;
  viewer.camera.aspect = width / height;
  viewer.camera.updateProjectionMatrix();
  viewer.renderer.setSize(width, height, false);
}

function clearRoomMeshes(group: THREE.Group): void {
  const viewer = roomViewer;
  if (viewer?.roomGroup === group) {
    viewer.windowMeshes.clear();
    viewer.selectedOutlines.clear();
    viewer.boundarySegments = [];
    viewer.previewObjects = [];
    viewer.fadedWindowIds.clear();
  }
  for (const child of [...group.children]) {
    group.remove(child);
    child.traverse((object) => {
      if (!(object instanceof THREE.Mesh || object instanceof THREE.LineSegments)) return;
      object.geometry.dispose();
      const materials = Array.isArray(object.material) ? object.material : [object.material];
      materials.forEach((material) => material.dispose());
    });
  }
}

type BoundaryLoop = { segments: RoomBoundarySegment[]; area: number };

function getClosedBoundaryLoops(segments: RoomBoundarySegment[]): BoundaryLoop[] {
  const remaining = segments.filter((segment) =>
    [segment.StartX, segment.StartY, segment.EndX, segment.EndY].every(Number.isFinite),
  );
  const loops: BoundaryLoop[] = [];
  const toleranceMm = 5;
  const near = (a: [number, number], b: [number, number]) => Math.hypot(a[0] - b[0], a[1] - b[1]) <= toleranceMm;

  while (remaining.length) {
    const first = remaining.shift()!;
    const start: [number, number] = [first.StartX, first.StartY];
    let end: [number, number] = [first.EndX, first.EndY];
    const points: Array<[number, number]> = [start, end];
    const loopSegments = [first];
    let closed = near(start, end);

    for (let guard = 0; !closed && guard <= segments.length; guard++) {
      const index = remaining.findIndex((segment) =>
        near(end, [segment.StartX, segment.StartY]) || near(end, [segment.EndX, segment.EndY]),
      );
      if (index < 0) break;
      const next = remaining.splice(index, 1)[0];
      const nextStart: [number, number] = [next.StartX, next.StartY];
      const nextEnd: [number, number] = [next.EndX, next.EndY];
      end = near(end, nextStart) ? nextEnd : nextStart;
      loopSegments.push(next);
      points.push(end);
      closed = near(end, start);
    }

    if (!closed || points.length < 4) continue;
    let twiceArea = 0;
    for (let i = 0; i < points.length - 1; i++) {
      twiceArea += points[i][0] * points[i + 1][1] - points[i + 1][0] * points[i][1];
    }
    loops.push({ segments: loopSegments, area: Math.abs(twiceArea / 2) });
  }
  return loops;
}

function getOuterBoundaryLoop(segments: RoomBoundarySegment[]): BoundaryLoop | null {
  return getClosedBoundaryLoops(segments).sort((a, b) => b.area - a.area)[0] ?? null;
}

function buildRoomShell(viewer: RoomViewer, outerSegments: RoomBoundarySegment[], ceilingHeightMm: number): boolean {
  const points = outerSegments.flatMap((segment) => [[segment.StartX, segment.StartY], [segment.EndX, segment.EndY]])
    .filter((point) => point.every(Number.isFinite));
  if (points.length < 4) return false;
  const centerX = (Math.min(...points.map((point) => point[0])) + Math.max(...points.map((point) => point[0]))) / 2;
  const centerY = (Math.min(...points.map((point) => point[1])) + Math.max(...points.map((point) => point[1]))) / 2;
  viewer.originX = centerX;
  viewer.originY = centerY;
  const ceilingHeight = ceilingHeightMm / 1000;
  const wallMaterial = new THREE.MeshBasicMaterial({ color: 0x86a9cc, transparent: true, opacity: 0.28, side: THREE.DoubleSide, depthWrite: false });
  const edgeMaterial = new THREE.LineBasicMaterial({ color: 0xb8cbe0, opacity: 0.9, transparent: true });
  let maxExtent = 0;
  for (const segment of outerSegments) {
    if (![segment.StartX, segment.StartY, segment.EndX, segment.EndY].every(Number.isFinite)) continue;
    const startX = (segment.StartX - centerX) / 1000;
    const startZ = (centerY - segment.StartY) / 1000;
    const endX = (segment.EndX - centerX) / 1000;
    const endZ = (centerY - segment.EndY) / 1000;
    maxExtent = Math.max(maxExtent, Math.abs(startX), Math.abs(startZ), Math.abs(endX), Math.abs(endZ));
    const dx = endX - startX, dz = endZ - startZ;
    const segmentLength = numberValue(segment.Length);
    const length = segmentLength != null && segmentLength > 0 ? segmentLength / 1000 : Math.hypot(dx, dz);
    if (!Number.isFinite(length) || length <= 0) continue;
    const geometry = new THREE.BoxGeometry(length, ceilingHeight, WALL_THICKNESS_MM / 1000);
    const wall = new THREE.Mesh(geometry, wallMaterial);
    wall.position.set((startX + endX) / 2, ceilingHeight / 2, (startZ + endZ) / 2);
    wall.rotation.y = Math.atan2(-dz, dx);
    viewer.roomGroup.add(wall);
    const edge = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), edgeMaterial);
    edge.position.copy(wall.position);
    edge.rotation.copy(wall.rotation);
    viewer.roomGroup.add(edge);
  }
  if (!viewer.roomGroup.children.length) return false;

  const grid = viewer.scene.children.find((child) => child instanceof THREE.GridHelper);
  if (grid) {
    viewer.scene.remove(grid);
    grid.geometry.dispose();
    (Array.isArray(grid.material) ? grid.material : [grid.material]).forEach((material) => material.dispose());
  }
  const gridSize = Math.max(10, Math.ceil(maxExtent * 2 / 5) * 5);
  viewer.scene.add(new THREE.GridHelper(gridSize, Math.max(10, gridSize), 0x5d6b7c, 0x39424e));
  const target = new THREE.Vector3(0, ceilingHeight / 2, 0);
  viewer.controls.target.copy(target);
  const radius = Math.sqrt(2 * maxExtent * maxExtent + (ceilingHeight / 2) ** 2);
  const distance = Math.max(2, radius / Math.sin(THREE.MathUtils.degToRad(viewer.camera.fov / 2)) * 1.15);
  viewer.camera.position.copy(target).addScaledVector(new THREE.Vector3(1, 0.8, -1).normalize(), distance);
  viewer.camera.near = Math.max(0.01, distance / 1000);
  viewer.camera.far = distance * 100;
  viewer.camera.updateProjectionMatrix();
  viewer.controls.update();
  return true;
}

function addSmokeZone(viewer: RoomViewer, segments: RoomBoundarySegment[], room: CheckedRoom): void {
  const bottomMm = numberValue(room.SmokeZoneBottom);
  const topMm = numberValue(room.SmokeZoneTop);
  if (bottomMm == null || topMm == null || topMm <= bottomMm) return;

  const height = (topMm - bottomMm) / 1000;
  const centerY = (bottomMm + topMm) / 2000;
  const material = new THREE.MeshBasicMaterial({
    color: 0x5ac5d8, transparent: true, opacity: 0.16,
    side: THREE.DoubleSide, depthWrite: false,
  });
  const edgeMaterial = new THREE.LineBasicMaterial({ color: 0x75afbb, transparent: true, opacity: 0.65, depthWrite: false });
  const zoneGroup = new THREE.Group();
  zoneGroup.name = "smoke-zone";

  for (const segment of segments) {
    if (![segment.StartX, segment.StartY, segment.EndX, segment.EndY].every(Number.isFinite)) continue;
    const sx = (segment.StartX - viewer.originX) / 1000;
    const sz = (viewer.originY - segment.StartY) / 1000;
    const ex = (segment.EndX - viewer.originX) / 1000;
    const ez = (viewer.originY - segment.EndY) / 1000;
    const dx = ex - sx, dz = ez - sz;
    const lengthValue = numberValue(segment.Length);
    const length = lengthValue != null && lengthValue > 0 ? lengthValue / 1000 : Math.hypot(dx, dz);
    if (!Number.isFinite(length) || length <= 0) continue;

    const geometry = new THREE.PlaneGeometry(length, height);
    const band = new THREE.Mesh(geometry, material);
    band.position.set((sx + ex) / 2, centerY, (sz + ez) / 2);
    // Place the band just outside the wall, farther from the room interior than its windows.
    const midpointX = (sx + ex) / 2, midpointZ = (sz + ez) / 2;
    const normal = new THREE.Vector2(-dz, dx).normalize();
    if (normal.x * midpointX + normal.y * midpointZ < 0) normal.negate();
    band.position.x += normal.x * 0.01;
    band.position.z += normal.y * 0.01;
    band.rotation.y = Math.atan2(-dz, dx);
    band.renderOrder = 1;
    zoneGroup.add(band);

    const edge = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), edgeMaterial);
    edge.position.copy(band.position);
    edge.rotation.copy(band.rotation);
    edge.renderOrder = 2;
    zoneGroup.add(edge);
  }
  viewer.roomGroup.add(zoneGroup);
}

function addRoomWindows(viewer: RoomViewer, room: CheckedRoom): void {
  const windows = Array.isArray(room.Windows) ? room.Windows : [];
  for (const win of windows) {
    const x = numberValue(win.LocationX), y = numberValue(win.LocationY);
    const widthMm = numberValue(win.Width), heightMm = numberValue(win.Height), sillMm = numberValue(win.SillHeight);
    if (x == null || y == null || widthMm == null || heightMm == null || sillMm == null || widthMm <= 0 || heightMm <= 0) continue;
    let nearest: RoomBoundarySegment | null = null, nearestDistance = Infinity, projectedX = x, projectedY = y;
    for (const segment of viewer.boundarySegments) {
      const vx = segment.EndX - segment.StartX, vy = segment.EndY - segment.StartY;
      const len2 = vx * vx + vy * vy;
      if (!len2) continue;
      const t = THREE.MathUtils.clamp(((x - segment.StartX) * vx + (y - segment.StartY) * vy) / len2, 0, 1);
      const px = segment.StartX + t * vx, py = segment.StartY + t * vy;
      const distance = Math.hypot(x - px, y - py);
      if (distance < nearestDistance) { nearest = segment; nearestDistance = distance; projectedX = px; projectedY = py; }
    }
    if (!nearest) continue;
    const vx = nearest.EndX - nearest.StartX, vy = nearest.EndY - nearest.StartY, len2 = vx * vx + vy * vy;
    const t = len2 ? THREE.MathUtils.clamp(((x - nearest.StartX) * vx + (y - nearest.StartY) * vy) / len2, 0, 1) : 0;
    projectedX = nearest.StartX + t * vx; projectedY = nearest.StartY + t * vy;
    const sx = (nearest.StartX - viewer.originX) / 1000, sz = (viewer.originY - nearest.StartY) / 1000;
    const ex = (nearest.EndX - viewer.originX) / 1000, ez = (viewer.originY - nearest.EndY) / 1000;
    const dx = ex - sx, dz = ez - sz, angle = Math.atan2(-dz, dx);
    const centerX = (projectedX - viewer.originX) / 1000, centerZ = (viewer.originY - projectedY) / 1000;
    const segmentMidX = (sx + ex) / 2, segmentMidZ = (sz + ez) / 2;
    const outward = new THREE.Vector2(-dz, dx).normalize();
    if (outward.x * segmentMidX + outward.y * segmentMidZ < 0) outward.negate();
    const mesh = new THREE.Mesh(new THREE.PlaneGeometry(widthMm / 1000, heightMm / 1000), new THREE.MeshBasicMaterial({
      color: windowDisplayColor(win), side: THREE.DoubleSide, transparent: true, opacity: 0.98, depthWrite: false,
    }));
    const wallSurfaceOffset = WALL_THICKNESS_MM / 2000 + 0.002;
    mesh.position.set(centerX + outward.x * wallSurfaceOffset, (sillMm + heightMm / 2) / 1000, centerZ + outward.y * wallSurfaceOffset);
    mesh.rotation.y = angle;
    mesh.renderOrder = 3;
    mesh.userData.window = win;
    viewer.roomGroup.add(mesh);
    viewer.windowMeshes.set(idKey(win.WindowId), mesh);
    console.debug("Smoke-exhaust window placement", {
      WindowId: win.WindowId,
      HostWallId: win.HostWallId,
      matchedSegment: { StartX: nearest.StartX, StartY: nearest.StartY, EndX: nearest.EndX, EndY: nearest.EndY },
      projectedX, projectedY, ThreeX: mesh.position.x, ThreeZ: mesh.position.z,
      rotationY: mesh.rotation.y,
    });
  }
  syncRoomWindowSelection(viewer);
}

function windowDisplayColor(win: WindowRow): number {
  if ((numberValue(win.EffectiveArea) ?? 0) > 0) return 0x38b66a;
  if ((numberValue(win.HeightInZone) ?? 0) <= 0) return 0x9aa3ad;
  if (win.OperationType === "fixed") return 0xe9b949;
  return 0x9aa3ad;
}

function syncRoomWindowSelection(viewer: RoomViewer): void {
  const selectedId = selectedWindow ? idKey(selectedWindow.WindowId) : "";
  for (const [id, mesh] of viewer.windowMeshes) {
    const old = viewer.selectedOutlines.get(id);
    if (old) { viewer.roomGroup.remove(old); old.geometry.dispose(); (old.material as THREE.Material).dispose(); viewer.selectedOutlines.delete(id); }
    (mesh.material as THREE.MeshBasicMaterial).color.setHex(windowDisplayColor(mesh.userData.window as WindowRow));
    if (id !== selectedId) continue;
    const outline = new THREE.LineSegments(new THREE.EdgesGeometry(mesh.geometry), new THREE.LineBasicMaterial({ color: 0x55a9ff }));
    outline.position.copy(mesh.position); outline.rotation.copy(mesh.rotation);
    outline.renderOrder = 4;
    viewer.roomGroup.add(outline); viewer.selectedOutlines.set(id, outline);
  }
}

function disposeRoomViewer(): void {
  const viewer = roomViewer;
  if (!viewer) return;
  roomViewer = null;
  viewer.requestId++;
  window.cancelAnimationFrame(viewer.animationFrame);
  viewer.resizeObserver?.disconnect();
  viewer.renderer.domElement.removeEventListener("click", viewer.clickHandler);
  viewer.controls.dispose();
  clearRoomMeshes(viewer.roomGroup);
  viewer.renderer.dispose();
  viewer.renderer.domElement.remove();
}

function nullableString(value: unknown): string | null {
  return typeof value === "string" ? value : null;
}

function nullableBoolean(value: unknown): boolean | null {
  return typeof value === "boolean" ? value : null;
}

function windowDiagramState(win: WindowRow): { key: string; label: string } {
  if (win.NeedsManualConfirm === true) return { key: "manual", label: "需人工確認" };
  if (win.IsInSmokeZone === false || (numberValue(win.HeightInZone) ?? 0) <= 0) {
    return { key: "outside", label: "未進入有效帶" };
  }
  if (win.OperationType === "fixed" && (numberValue(win.AreaInZone) ?? 0) > 0) {
    return { key: "fixed", label: "位於有效帶，但無法開啟" };
  }
  if ((numberValue(win.EffectiveArea) ?? 0) > 0) return { key: "contributing", label: "可貢獻有效開口" };
  return { key: "outside", label: "未進入有效帶" };
}

function renderWindowHeightDiagram(room: CheckedRoom): void {
  const panel = el("section", "panel height-diagram-panel");
  panel.append(el("div", "section-heading", "窗戶高度與有效帶關係示意"));
  panel.append(el("p", "height-diagram-subtitle", "比較各窗戶與有效排煙帶的垂直高度關係，非實際牆面位置。"));

  const windows = Array.isArray(room.Windows) ? room.Windows : [];
  const ceilingHeight = numberValue(room.CeilingHeight);
  const zoneBottom = numberValue(room.SmokeZoneBottom);
  const zoneTop = numberValue(room.SmokeZoneTop);
  if (!windows.length || ceilingHeight == null || ceilingHeight <= 0 || zoneBottom == null || zoneTop == null) {
    panel.append(el("div", "empty-state", "缺少樓層高度或窗戶資料，無法繪製高度示意。"));
    root.append(panel);
    return;
  }

  const plotTop = 54;
  const floorY = 278;
  const plotHeight = floorY - plotTop;
  const left = 220;
  const windowOffset = 150;
  const columnWidth = 116;
  const width = left + windowOffset + windows.length * columnWidth + 12;
  const yForHeight = (height: number) => floorY - (height / ceilingHeight) * plotHeight;
  const clampY = (y: number) => Math.max(plotTop, Math.min(floorY, y));
  const svgNS = "http://www.w3.org/2000/svg";
  const svg = document.createElementNS(svgNS, "svg");
  svg.classList.add("height-diagram-svg");
  svg.setAttribute("viewBox", `0 0 ${width} 350`);
  svg.setAttribute("width", String(width));
  svg.setAttribute("height", "350");
  svg.setAttribute("role", "group");
  svg.setAttribute("aria-label", "窗戶高度與有效排煙帶關係示意圖");

  const addSvg = (tag: string, attrs: Record<string, string>, text?: string): SVGElement => {
    const node = document.createElementNS(svgNS, tag);
    for (const [key, value] of Object.entries(attrs)) node.setAttribute(key, value);
    if (text != null) node.textContent = text;
    svg.append(node);
    return node;
  };
  const zoneTopY = clampY(yForHeight(zoneTop));
  const zoneBottomY = clampY(yForHeight(zoneBottom));
  addSvg("rect", { x: String(left - 10), y: String(zoneTopY), width: String(width - left - 2), height: String(Math.max(0, zoneBottomY - zoneTopY)), class: "height-diagram-zone" });
  addSvg("line", { x1: String(left - 10), y1: String(floorY), x2: String(width - 10), y2: String(floorY), class: "height-diagram-ground" });
  addSvg("line", { x1: String(left - 10), y1: String(plotTop), x2: String(width - 10), y2: String(plotTop), class: "height-diagram-ceiling" });
  addSvg("line", { x1: "150", y1: String(plotTop), x2: "150", y2: String(floorY), class: "height-diagram-axis" });
  addSvg("line", { x1: "145", y1: String(floorY), x2: "155", y2: String(floorY), class: "height-diagram-tick" });
  addSvg("line", { x1: "145", y1: String(zoneBottomY), x2: "155", y2: String(zoneBottomY), class: "height-diagram-tick" });
  addSvg("line", { x1: "145", y1: String(zoneTopY), x2: "155", y2: String(zoneTopY), class: "height-diagram-tick" });
  addSvg("text", { x: "138", y: String(floorY + 4), class: "height-diagram-axis-label", "text-anchor": "end" }, "0 mm");
  addSvg("text", { x: "138", y: String(zoneBottomY + 4), class: "height-diagram-axis-label", "text-anchor": "end" }, `${fmt(zoneBottom, 1)} mm`);
  addSvg("text", { x: "138", y: String(Math.max(12, zoneTopY - 6)), class: "height-diagram-axis-label", "text-anchor": "end" }, `${fmt(zoneTop, 1)} mm`);
  addSvg("text", { x: String(left), y: String(plotTop - 22), class: "height-diagram-ceiling-label" }, `天花板 ${fmt(ceilingHeight, 1)} mm`);
  addSvg("text", { x: String(left + 8), y: String(zoneTopY + 16), class: "height-diagram-zone-label" }, "有效排煙帶");
  addSvg("text", { x: String(left + 8), y: String(zoneTopY + 31), class: "height-diagram-zone-label" }, `${fmt(zoneBottom, 1)}–${fmt(zoneTop, 1)} mm`);
  addSvg("text", { x: String(left), y: "340", class: "height-diagram-note" }, "窗戶水平排列僅供比較，不代表實際牆面位置");

  windows.forEach((win, index) => {
    const x = left + windowOffset + index * columnWidth + 21;
    const sill = numberValue(win.SillHeight);
    const head = numberValue(win.HeadHeight);
    const heightInZone = numberValue(win.HeightInZone) ?? 0;
    const shownTop = head == null ? null : Math.min(head, ceilingHeight);
    const state = windowDiagramState(win);
    const isSelected = selectedWindow != null && idKey(selectedWindow.WindowId) === idKey(win.WindowId);
    const group = document.createElementNS(svgNS, "g");
    group.classList.add("height-diagram-window", `state-${state.key}`);
    if (isSelected) group.classList.add("selected");
    group.setAttribute("tabindex", "0");
    group.setAttribute("role", "button");
    group.setAttribute("aria-label", `選取窗戶 ${textValue(win.WindowId)}，${state.label}`);
    const title = document.createElementNS(svgNS, "title");
    title.textContent = [
      `窗戶 ID：${textValue(win.WindowId)}`,
      `Family / Type: ${textValue(win.FamilyName)} / ${textValue(win.TypeName)}`,
      `窗台高度: ${fmt(win.SillHeight, 1)} mm`,
      `窗頂高度: ${fmt(win.HeadHeight, 1)} mm`,
      `有效帶內高度: ${fmt(win.HeightInZone, 1)} mm`,
      `有效帶內面積: ${fmt(win.AreaInZone)} m²`,
      `開啟比例: ${fmtRatio(win.OpeningRatio)}`,
      `有效開口面積: ${fmt(win.EffectiveArea)} m²`,
      `診斷狀態: ${state.label}${win.Note ? `；${String(win.Note)}` : ""}`,
      `HostWallId: ${textValue(win.HostWallId)}`,
    ].join("\n");
    group.append(title);

    if (sill != null && shownTop != null && shownTop > sill) {
      const topY = clampY(yForHeight(shownTop));
      const sillY = clampY(yForHeight(sill));
      const rect = document.createElementNS(svgNS, "rect");
      rect.setAttribute("x", String(x));
      rect.setAttribute("y", String(topY));
      rect.setAttribute("width", "48");
      rect.setAttribute("height", String(Math.max(1, sillY - topY)));
      rect.setAttribute("rx", "3");
      rect.classList.add("height-diagram-window-body");
      group.append(rect);

      if (heightInZone > 0) {
        const overlapBottom = shownTop - heightInZone;
        const overlapY = clampY(yForHeight(overlapBottom));
        const overlap = document.createElementNS(svgNS, "rect");
        overlap.setAttribute("x", String(x + 2));
        overlap.setAttribute("y", String(topY + 1));
        overlap.setAttribute("width", "44");
        overlap.setAttribute("height", String(Math.max(0, overlapY - topY - 1)));
        overlap.setAttribute("rx", "2");
        overlap.classList.add("height-diagram-overlap");
        group.append(overlap);
      }
    }
    const idText = document.createElementNS(svgNS, "text");
    idText.setAttribute("x", String(x + 24));
    idText.setAttribute("y", String(floorY + 18));
    idText.setAttribute("text-anchor", "middle");
    idText.classList.add("height-diagram-window-id");
    idText.textContent = textValue(win.WindowId);
    group.append(idText);
    const ratioText = document.createElementNS(svgNS, "text");
    ratioText.setAttribute("x", String(x + 24));
    ratioText.setAttribute("y", String(floorY + 34));
    ratioText.setAttribute("text-anchor", "middle");
    ratioText.classList.add("height-diagram-ratio");
    ratioText.textContent = fmtRatio(win.OpeningRatio);
    group.append(ratioText);

    const select = () => { selectedWindow = win; render(); };
    group.addEventListener("click", select);
    group.addEventListener("keydown", (event) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        select();
      }
    });
    svg.append(group);
  });

  const scroller = el("div", "height-diagram-scroll");
  scroller.append(svg);
  panel.append(scroller);
  const legend = el("div", "height-diagram-legend");
  const legendRows: Array<[string, string]> = [
    ["outside", "未進入有效帶"], ["fixed", "固定窗／無法開啟"],
    ["contributing", "可貢獻有效開口"], ["manual", "需人工確認"], ["selected", "目前選取"],
  ];
  for (const [key, label] of legendRows) {
    const item = el("span", "height-diagram-legend-item");
    item.append(el("i", `height-diagram-swatch ${key}`), el("span", "", label));
    legend.append(item);
  }
  panel.append(legend);

  if (selectedWindow) {
    const selectedId = validWindowId(selectedWindow.WindowId);
    const locateRow = el("div", "height-diagram-actions");
    locateRow.append(el("span", "", `已選取窗戶 ${textValue(selectedWindow.WindowId)}`));
    if (selectedId != null) {
      const locateButton = el("button", "diagram-locate-button", "在 Revit 中定位") as HTMLButtonElement;
      locateButton.type = "button";
      locateButton.disabled = locatingWindowId != null;
      locateButton.addEventListener("click", () => { void locateWindow(selectedId); });
      locateRow.append(locateButton);
    }
    panel.append(locateRow);
  }
  root.append(panel);
}

function renderRoomImprovementPlan(room: CheckedRoom): void {
  const summary = object(room.Summary);
  const recommendations = Array.isArray(summary.Recommendations)
    ? summary.Recommendations.filter((item): item is Dict => !!item && typeof item === "object")
    : [];

  const plan = el("section", "panel improvement-plan");
  plan.append(el("div", "section-heading", "房間改善方案"));

  renderWindowPlanPanel(plan, room);
  const current = el("div", "plan-step");
  current.append(el("h3", "", "目前狀態"));
  appendPlanMetrics(current, [
    ["目前房間檢核結果", textValue(summary.Result, "本次未檢核"), statusClass(textValue(summary.Result, "本次未檢核"))],
    ["需求有效開口面積", `${fmt(summary.RequiredArea)} m²`],
    ["目前有效開口面積", `${fmt(summary.TotalEffectiveArea)} m²`],
    ["不足面積", `${fmt(summary.Deficit)} m²`],
  ]);
  plan.append(current);
  if (!recommendations.length) {
    plan.append(el("p", "scenario-note", summary.Result === "PASS" ? "目前狀態已符合需求，沒有改善步驟。" : "目前沒有可列出的改善步驟。"));
    root.append(plan);
    return;
  }

  const typeA = recommendations.find((item) => item.Type === "A");
  const typeB = recommendations.find((item) => item.Type === "B");
  const typeC = recommendations.find((item) => item.Type === "C");
  const typeD = recommendations.find((item) => item.Type === "D");

  if (typeA) {
    const step = el("div", "plan-step");
    step.append(el("h3", "", "步驟 1｜優先改善既有固定窗"));
    const fixedWindows = Array.isArray(typeA.FixedWindows)
      ? typeA.FixedWindows.filter((item): item is Dict => !!item && typeof item === "object")
      : [];
    if (fixedWindows.length) {
      const tableWrap = el("div", "table-wrap");
      const table = el("table", "data-table");
      const headings = ["窗戶 ID", "目前窗型", "有效帶內面積", "可增加有效面積"];
      const thead = el("thead");
      const headerRow = el("tr");
      for (const heading of headings) headerRow.append(el("th", "", heading));
      thead.append(headerRow);
      table.append(thead);
      const tbody = el("tbody");
      for (const win of fixedWindows) {
        const row = el("tr");
        for (const value of [
          textValue(win.WindowId), textValue(win.CurrentType), `${fmt(win.AreaInZone)} m²`, `${fmt(win.PotentialGain)} m²`,
        ]) row.append(el("td", "", value));
        tbody.append(row);
      }
      table.append(tbody);
      tableWrap.append(table);
      step.append(tableWrap);
    }
    const result = textValue(typeA.Result, "未提供");
    appendPlanMetrics(step, [
      ["改善後總有效面積", `${fmt(typeA.ProjectedTotalEffectiveArea)} m²`],
      ["改善後不足面積", `${fmt(typeA.ProjectedDeficit)} m²`],
      ["改善後房間檢核結果", result, statusClass(result)],
    ]);
    const typeANote = typeA.CanSolve === true
      ? "僅改善既有固定窗即可滿足需求。"
      : `改善既有固定窗後仍不足 ${fmt(typeA.ProjectedDeficit)} m²；若仍為 FAIL，接續步驟 2｜新增有效開口。`;
    step.append(el("p", "scenario-note", typeANote));
    plan.append(step);
  }

  const typeAStillFails = typeA
    ? typeA.Result === "FAIL"
    : summary.Result === "FAIL";
  if (typeAStillFails && typeC) {
    const step = el("div", "plan-step");
    step.append(el("h3", "", "步驟 2｜新增有效開口"));
    step.append(el("p", "scenario-note", "以下為依窗型有效面積估算的候選方案，實際位置仍需於 Revit 中確認。"));
    const availableTypes = Array.isArray(typeC.AvailableWindowTypes)
      ? typeC.AvailableWindowTypes.filter((item): item is Dict => !!item && typeof item === "object")
      : [];
    const cards = el("div", "scenario-cards");
    step.append(el("p", "scenario-note", "候選方案的 PASS 僅為有效面積估算，實際可配置位置與可執行狀態請於 3D 預覽中確認。"));
    for (const candidate of availableTypes) {
      const card = el("div", "scenario-card");
      card.append(el("h4", "", textValue(candidate.TypeName)));
      card.append(el("p", "scenario-note", `${fmt(candidate.Width, 0)} × ${fmt(candidate.Height, 0)} mm`));
      const result = textValue(candidate.Result, "未提供");
      appendPlanMetrics(card, [
        ["開啟比例", fmtRatio(candidate.OpeningRatio)],
        ["單扇有效面積", `${fmt(candidate.EffectiveAreaPerWindow)} m²`],
        ["估算需要", `${textValue(candidate.RequiredCount, "—")} 扇`],
        ["改善後總有效面積", `${fmt(candidate.ProjectedTotalEffectiveArea)} m²`],
        ["剩餘缺口", `${fmt(candidate.ProjectedDeficit)} m²`],
        ["面積估算結果", result === "PASS" ? "預估通過" : result, statusClass(result)],
      ]);
      const previewButton = el("button", "window-plan-button", windowPlanBusy ? "正在建立預覽…" : "3D 預覽") as HTMLButtonElement;
      previewButton.type = "button";
      previewButton.disabled = windowPlanBusy || windowOperationId != null || summary.Result !== "FAIL";
      previewButton.addEventListener("click", () => { void previewSmokeWindowPlan(room, candidate); });
      card.append(previewButton);
      cards.append(card);
    }
    if (!availableTypes.length) step.append(el("p", "scenario-note", "目前沒有可列出的候選窗型。"));
    else step.append(cards);
    plan.append(step);
  }

  if (typeB) {
    const other = el("div", "plan-step");
    other.append(el("h3", "", "其他可改善項目"));
    other.append(el("p", "scenario-note", `接近有效帶的窗戶：${textValue(typeB.WindowCount, "0")} 扇`));
    const nearWindows = Array.isArray(typeB.NearWindows)
      ? typeB.NearWindows.filter((item): item is Dict => !!item && typeof item === "object")
      : [];
    if (nearWindows.length) {
      const list = el("ul", "plan-list");
      for (const win of nearWindows) {
        list.append(el("li", "", `窗戶 ${textValue(win.WindowId)} · ${textValue(win.CurrentType)} · ${textValue(win.SuggestAction)}`));
      }
      other.append(list);
    }
    other.append(el("p", "", textValue(typeB.Note)));
    plan.append(other);
  }

  if (typeD) {
    const alternative = el("div", "plan-step alternative-plan");
    alternative.append(el("h3", "", "其他替代策略｜機械排煙"));
    appendPlanMetrics(alternative, [["目前自然排煙不足", `${fmt(typeD.Deficit)} m²`]]);
    alternative.append(el("p", "scenario-note", "若既有窗改善或新增自然排煙開口仍受空間條件限制，可進一步評估機械排煙方案（排煙風機＋風管）。"));
    plan.append(alternative);
  }

  root.append(plan);
}

function appendPlanMetrics(container: HTMLElement, rows: Array<[string, string, string?]>): void {
  const metrics = el("div", "plan-metrics");
  for (const [label, value, valueClass] of rows) {
    const item = el("div", "plan-metric");
    item.append(el("span", "", label), el("strong", valueClass ?? "", value));
    metrics.append(item);
  }
  container.append(metrics);
}

function renderWindowPlanPanel(container: HTMLElement, room: CheckedRoom): void {
  const roomId = idKey(room.RoomId);
  if (windowPlanError) container.append(el("p", "error-message", windowPlanError));
  if (windowPlanMessage) container.append(el("p", "window-plan-message", windowPlanMessage));
  if (windowPlanPreview && windowPlanPreview.roomId === roomId) {
    const predicted = windowPlanPreview.predictedResult;
    const preview = el("div", "window-plan-preview");
    preview.append(el("h4", "", `預覽方案：${windowPlanPreview.windowType}`));
    appendPlanMetrics(preview, [
      ["面積估算", windowPlanPreview.areaEstimateResult, statusClass(windowPlanPreview.areaEstimateResult)],
      ["實際配置", windowPlanPreview.canApply ? "可執行" : "不可執行", windowPlanPreview.canApply ? "badge pass" : "badge fail"],
      ["符合配置條件的位置", `${windowPlanPreview.foundPositions} / ${windowPlanPreview.requestedCount}`],
      ...(predicted.CurrentEffectiveArea != null ? [["目前有效面積", `${fmt(predicted.CurrentEffectiveArea)} m²`] as [string, string]] : []),
      ...(predicted.AddedEffectiveArea != null ? [["新增有效面積", `${fmt(predicted.AddedEffectiveArea)} m²`] as [string, string]] : []),
      ["改善後總有效面積", `${fmt(predicted.TotalEffectiveArea)} m²`],
      ["需求有效開口面積", `${fmt(predicted.RequiredArea)} m²`],
    ]);
    const diagnosticCount = Math.min(windowPlanPreview.diagnosticPlacements.length,
      Math.max(0, windowPlanPreview.requestedCount - windowPlanPreview.placements.length));
    preview.append(el("p", "scenario-note", `已在 3D 中預覽 ${windowPlanPreview.placements.length + diagnosticCount} 扇窗；尚未修改 Revit。`));
    if (!windowPlanPreview.canApply) {
      preview.append(el("p", "scenario-note", `面積估算：${windowPlanPreview.areaEstimateResult}；實際配置：不可執行。此方案需要 ${windowPlanPreview.requestedCount} 扇，但目前僅找到 ${windowPlanPreview.foundPositions} 個符合配置條件的位置。3D 中的紅色窗為「診斷預覽」，用來呈現若嘗試配置此方案時可能產生的衝突，不代表可直接套用至 Revit。`));
      const diagnosticRows = windowPlanPreview.diagnosticPlacements.slice(0, diagnosticCount);
      if (diagnosticRows.length) {
        preview.append(el("h4", "", "不可執行原因"));
        const list = el("ul", "plan-list");
        diagnosticRows.forEach((placement, index) => {
          const reasons = placement.conflictReasons?.length ? placement.conflictReasons.join("、") : "未提供衝突細節";
          list.append(el("li", "", `診斷預覽窗 ${index + 1}：${reasons}`));
        });
        preview.append(list);
      }
    }
    const actions = el("div", "window-plan-actions");
    const cancel = el("button", "window-plan-button secondary", "取消預覽") as HTMLButtonElement;
    cancel.type = "button"; cancel.disabled = windowPlanBusy;
    cancel.addEventListener("click", cancelWindowPlanPreview);
    const apply = el("button", "window-plan-button primary", windowPlanBusy ? "正在套用…" : "套用至 Revit") as HTMLButtonElement;
    apply.type = "button"; apply.disabled = windowPlanBusy || !windowPlanPreview.canApply ||
      !windowPlanPreview.placementSufficient || windowPlanPreview.foundPositions < windowPlanPreview.requestedCount ||
      windowPlanPreview.placements.length < windowPlanPreview.requestedCount || !windowPlanPreview.planId;
    apply.addEventListener("click", () => { void applyWindowPlanPreview(); });
    actions.append(cancel, apply); preview.append(actions); container.append(preview);
  }
  const hasFixedWindows = Array.isArray(room.Windows) && room.Windows.some((w) => (numberValue(w.OpeningRatio) ?? 0) === 0 && textValue(w.OperationType) === "fixed");
  if ((windowPlanInsufficient || hasFixedWindows) && textValue(object(room.Summary).Result) === "FAIL" && !windowRetrofitPreview && !windowReplacementPreview) {
    const fallback = el("div", "plan-step alternative-plan");
    fallback.append(el("h3", "", "新增開口不足，建議改善既有可開啟窗"));
    fallback.append(el("p", "scenario-note", "目前外牆沒有足夠空間新增所需開口，建議改善既有可開啟窗。"));
    const retrofitButton = el("button", "window-plan-button primary", windowPlanBusy ? "正在計算改善方案…" : "預覽改善既有窗（同族 56\" x 60\"，窗台高度 1590 mm）") as HTMLButtonElement;
    retrofitButton.type = "button"; retrofitButton.disabled = windowPlanBusy;
    if (hasFixedWindows) retrofitButton.textContent = windowPlanBusy ? "正在計算改善方案…" : "固定窗改善：預覽替換 3 扇（56\" x 62\"，窗台高度 1539 mm）";
    retrofitButton.addEventListener("click", () => {
      void (hasFixedWindows ? previewWindowReplacement(room) : previewWindowRetrofit(room));
    });
    fallback.append(retrofitButton); container.append(fallback);
  }
  if (windowReplacementPreview && windowReplacementPreview.roomId === roomId) {
    const replacement = windowReplacementPreview;
    const preview = el("div", "window-plan-preview retrofit-preview");
    preview.append(el("h4", "", `固定窗改善：${replacement.targetType}`));
    preview.append(el("p", "scenario-note", `目前固定窗 ${room.Windows?.filter((w) => textValue(w.OperationType) === "fixed").length ?? 0} 扇；開啟比例為 0%，總有效開口面積 ${fmt(replacement.current)} m²，FAIL`));
    preview.append(el("p", "scenario-note", `建議替換 ${replacement.sources.length} 扇，窗台高度 1539 mm；改善後總有效開口面積 ${fmt(replacement.total)} m²，需求有效開口面積 ${fmt(replacement.required)} m²，${replacement.result}`));
    appendPlanMetrics(preview, [["修改窗數", String(replacement.sources.length)], ["每扇有效開口面積", `${fmt(replacement.proposed[0]?.EffectiveArea)} m²`], ["總有效開口面積", `${fmt(replacement.total)} m²`], ["需求有效開口面積", `${fmt(replacement.required)} m²`], ["替換安全檢查", replacement.safe ? "通過" : "未通過"], ["預測結果", replacement.result, statusClass(replacement.result)]]);
    if (replacement.blockingWarnings.length) {
      preview.append(el("h4", "", "阻止套用"));
      for (const warning of replacement.blockingWarnings) preview.append(el("p", "scenario-note", warning));
    }
    if (replacement.informationalWarnings.length) {
      preview.append(el("h4", "", "僅提示資訊"));
      for (const warning of replacement.informationalWarnings) preview.append(el("p", "scenario-note", warning));
    }
    const actions = el("div", "window-plan-actions");
    const cancel = el("button", "window-plan-button secondary", "取消預覽") as HTMLButtonElement;
    cancel.type = "button"; cancel.disabled = windowPlanBusy; cancel.addEventListener("click", () => { windowReplacementPreview = null; clearWindowPlanPreviewMeshes(); render(); });
    const apply = el("button", "window-plan-button primary", "套用至 Revit") as HTMLButtonElement;
    apply.type = "button"; apply.disabled = windowPlanBusy || !replacement.canApply; apply.addEventListener("click", () => { void applyWindowReplacementPreview(); });
    actions.append(cancel, apply); preview.append(actions); container.append(preview);
  }
  if (windowRetrofitPreview && windowRetrofitPreview.roomId === roomId) {
    const retrofit = windowRetrofitPreview;
    const preview = el("div", "window-plan-preview retrofit-preview");
    preview.append(el("h4", "", `改善既有窗預覽：${retrofit.windowType}`));
    const originalType = retrofit.originalWindows[0];
    const proposedType = retrofit.proposedWindows[0];
    preview.append(el("p", "scenario-note", `目前：${textValue(originalType?.FamilyName)}: ${textValue(originalType?.TypeName)}；窗台高度 ${fmt(originalType?.SillHeightMm, 1)} mm；總有效開口面積 ${fmt(retrofit.currentTotalEffectiveArea)} m²；FAIL`));
    preview.append(el("p", "scenario-note", `改善後：${textValue(proposedType?.FamilyName)}: ${textValue(proposedType?.TypeName)}；窗台高度 ${fmt(proposedType?.SillHeightMm, 1)} mm；總有效開口面積 ${fmt(retrofit.proposedTotalEffectiveArea)} m²；${retrofit.predictedResult}`));
    appendPlanMetrics(preview, [
      ["目前總有效開口面積", `${fmt(retrofit.currentTotalEffectiveArea)} m²`],
      ["改善後總有效開口面積", `${fmt(retrofit.proposedTotalEffectiveArea)} m²`],
      ["需求有效開口面積", `${fmt(retrofit.requiredArea)} m²`],
      ["改善後 Deficit", `${fmt(retrofit.deficit)} m²`],
      ["Head <= Ceiling", retrofit.headWithinCeiling ? "是" : "否"],
      ["改善後結果", retrofit.predictedResult, statusClass(retrofit.predictedResult)],
    ]);
    preview.append(el("p", "scenario-note", `原窗位置與宿主牆保持不變；紫色半透明窗為預覽，窗頂不超過天花板高度 ${fmt(retrofit.ceilingHeightMm, 1)} mm。`));
    const actions = el("div", "window-plan-actions");
    const cancel = el("button", "window-plan-button secondary", "取消預覽") as HTMLButtonElement;
    cancel.type = "button"; cancel.disabled = windowPlanBusy; cancel.addEventListener("click", cancelWindowRetrofitPreview);
    const apply = el("button", "window-plan-button primary", windowPlanBusy ? "正在套用…" : "套用至 Revit") as HTMLButtonElement;
    apply.type = "button"; apply.disabled = windowPlanBusy || !retrofit.canApply;
    apply.addEventListener("click", () => { void applyWindowRetrofitPreview(); });
    actions.append(cancel, apply); preview.append(actions); container.append(preview);
  }
  if (windowOperationId && windowOperationRoomId === roomId) {
    const undo = el("button", "window-plan-button secondary", windowPlanBusy ? "正在復原…" : "復原至套用前狀態") as HTMLButtonElement;
    undo.type = "button"; undo.disabled = windowPlanBusy;
    undo.addEventListener("click", () => { void undoWindowPlanOperation(); });
    container.append(undo);
  }
  if (retrofitOperationId && retrofitOperationRoomId === roomId) {
    const undo = el("button", "window-plan-button secondary", windowPlanBusy ? "正在復原…" : "復原既有窗原始狀態") as HTMLButtonElement;
    undo.type = "button"; undo.disabled = windowPlanBusy;
    undo.addEventListener("click", () => { void undoWindowRetrofitOperation(); });
    container.append(undo);
  }
  if (replacementOperationId && replacementOperationRoomId === roomId) {
    const undo = el("button", "window-plan-button secondary", "復原固定窗替換") as HTMLButtonElement;
    undo.type = "button"; undo.disabled = windowPlanBusy; undo.addEventListener("click", () => { void undoWindowReplacementOperation(); }); container.append(undo);
  }
}

async function previewSmokeWindowPlan(room: CheckedRoom, candidate: Dict): Promise<void> {
  if (windowPlanBusy || textValue(object(room.Summary).Result) !== "FAIL") return;
  const requestedCount = numberValue(candidate.RequiredCount);
  if (requestedCount == null || !Number.isInteger(requestedCount) || requestedCount < 1) {
    windowPlanError = "Candidate has no valid RequiredCount; placement planning was skipped."; render(); return;
  }
  windowPlanBusy = true; windowPlanError = ""; windowPlanMessage = "";
  windowPlanPreview = null; clearWindowPlanPreviewMeshes(); render();
  try {
    const result = object(await callAllowedTool("plan_smoke_exhaust_window_plan", {
      roomId: Number(room.RoomId), windowType: textValue(candidate.TypeName), count: requestedCount,
      sillHeightMm: Math.max(0, (numberValue(room.SmokeZoneTop) ?? numberValue(room.CeilingHeight) ?? 0) - (numberValue(candidate.Height) ?? 0)),
      widthMm: numberValue(candidate.Width), heightMm: numberValue(candidate.Height),
    }));
    const placements = Array.isArray(result.Placements) ? result.Placements.map((row) => {
      const item = object(row);
      return {
        hostWallId: numberValue(item.HostWallId) ?? numberValue(item.hostWallId) ?? 0,
        locationX: numberValue(item.LocationX) ?? numberValue(item.locationX) ?? 0,
        locationY: numberValue(item.LocationY) ?? numberValue(item.locationY) ?? 0,
        sillHeightMm: numberValue(item.SillHeightMm) ?? numberValue(item.sillHeightMm) ?? 0,
        widthMm: numberValue(item.WidthMm) ?? numberValue(item.widthMm) ?? 0,
        heightMm: numberValue(item.HeightMm) ?? numberValue(item.heightMm) ?? 0,
      } satisfies PlannedPlacement;
    }) : [];
    const diagnosticPlacements = Array.isArray(result.DiagnosticPlacements) ? result.DiagnosticPlacements.map((row) => {
      const item = object(row);
      return {
        hostWallId: numberValue(item.HostWallId) ?? numberValue(item.hostWallId) ?? 0,
        locationX: numberValue(item.LocationX) ?? numberValue(item.locationX) ?? numberValue(item.X) ?? 0,
        locationY: numberValue(item.LocationY) ?? numberValue(item.locationY) ?? numberValue(item.Y) ?? 0,
        sillHeightMm: numberValue(item.SillHeightMm) ?? numberValue(item.sillHeightMm) ?? numberValue(item.SillHeight) ?? 0,
        widthMm: numberValue(item.WidthMm) ?? numberValue(item.widthMm) ?? numberValue(item.Width) ?? 0,
        heightMm: numberValue(item.HeightMm) ?? numberValue(item.heightMm) ?? numberValue(item.Height) ?? 0,
        isDiagnostic: true,
        conflictReasons: Array.isArray(item.ConflictReasons) ? item.ConflictReasons.filter((reason): reason is string => typeof reason === "string") : [],
      } satisfies PlannedPlacement;
    }) : [];
    const viewer = roomViewer;
    if (!viewer || viewer.roomId !== idKey(room.RoomId)) throw new Error("所選房間的 3D 檢視器尚未就緒。");
    if (!viewer.boundarySegments.length) {
      const info = object(await callAllowedTool("get_room_info", { roomId: Number(room.RoomId) }));
      viewer.boundarySegments = Array.isArray(info.BoundarySegments) ? info.BoundarySegments as RoomBoundarySegment[] : [];
      const points = viewer.boundarySegments.flatMap((segment) => [[segment.StartX, segment.StartY], [segment.EndX, segment.EndY]]);
      if (points.length) {
        viewer.originX = (Math.min(...points.map((point) => point[0])) + Math.max(...points.map((point) => point[0]))) / 2;
        viewer.originY = (Math.min(...points.map((point) => point[1])) + Math.max(...points.map((point) => point[1]))) / 2;
      }
    }
    const foundPositions = numberValue(result.FoundPositions) ?? placements.length;
    const placementSufficient = result.PlacementSufficient === true;
    const predictedResult = object(result.PredictedResult);
    const areaEstimateResult = textValue(candidate.Result, "未提供");
    const placementComplete = placementSufficient && foundPositions >= requestedCount && placements.length >= requestedCount && !!result.PlanId;
    const canApply = placementComplete && textValue(predictedResult.Result, areaEstimateResult) === "PASS";
    const missingCount = Math.max(0, requestedCount - placements.length);
    const displayPlacements = [
      ...placements.map((placement) => ({ ...placement, isDiagnostic: false })),
      ...diagnosticPlacements.slice(0, missingCount),
    ];
    addWindowPlanPreviewMeshes(viewer, displayPlacements);
    windowPlanInsufficient = !placementComplete;
    windowPlanMessage = windowPlanInsufficient
      ? `面積估算：${areaEstimateResult}；實際配置：不可執行。此方案需要 ${requestedCount} 扇，但目前僅找到 ${foundPositions} 個符合配置條件的位置。3D 中的紅色窗為診斷預覽，用來呈現若嘗試配置此方案時可能產生的衝突，不代表可直接套用至 Revit。`
      : "";
    windowPlanPreview = {
      planId: typeof result.PlanId === "string" ? result.PlanId : "", roomId: idKey(room.RoomId),
      windowType: textValue(result.WindowType, textValue(candidate.TypeName)), placements, diagnosticPlacements,
      requestedCount, foundPositions, placementSufficient, areaEstimateResult,
      predictedResult: Object.keys(predictedResult).length ? predictedResult : {
        TotalEffectiveArea: candidate.ProjectedTotalEffectiveArea,
        RequiredArea: object(room.Summary).RequiredArea,
        Deficit: candidate.ProjectedDeficit,
        Result: areaEstimateResult,
      }, canApply,
    };
  } catch (error) {
    windowPlanPreview = null; clearWindowPlanPreviewMeshes();
    windowPlanError = error instanceof Error ? error.message : String(error);
  } finally {
    windowPlanBusy = false; render();
  }
}

function getRetrofitTargetWindowIds(room: CheckedRoom): number[] {
  const windows = Array.isArray(room.Windows) ? room.Windows.filter((row) => (numberValue(row.OpeningRatio) ?? 0) > 0 && validWindowId(row.WindowId) != null) : [];
  const groups = new Map<string, WindowRow[]>();
  for (const win of windows) {
    const key = idKey(win.HostWallId);
    groups.set(key, [...(groups.get(key) ?? []), win]);
  }
  const candidates = [...groups.values()].sort((a, b) => b.length - a.length)[0] ?? [];
  return candidates.slice(0, 2).map((win) => validWindowId(win.WindowId)!).filter((id) => id != null);
}

async function previewWindowReplacement(room: CheckedRoom): Promise<void> {
  if (windowPlanBusy) return;
  windowPlanBusy = true; windowPlanError = ""; windowReplacementPreview = null; clearWindowPlanPreviewMeshes(); render();
  try {
    const result = object(await callAllowedTool("plan_smoke_exhaust_window_replacement", {
      roomId: Number(room.RoomId), targetWindowType: "Window-Double-Hung-Double: 56\" x 62\"",
      targetSillHeightMm: 1539, requiredCount: 3,
    }));
    if (result.Success !== true || !result.PlanId) throw new Error(textValue(result.Error, "替換規劃器未回傳方案。"));
    const sources = Array.isArray(result.SourceSnapshots) ? result.SourceSnapshots.map((row) => {
      const item = object(row); return { ...item, WindowId: item.ElementId, FamilyName: item.Family };
    }) : [];
    const proposed = Array.isArray(result.ProposedWindows) ? result.ProposedWindows.map((row) => {
      const item = object(row); return { ...item, WindowId: item.ElementId, FamilyName: item.Family, WidthMm: object(result.TargetType).WidthMm, HeightMm: object(result.TargetType).HeightMm };
    }) : [];
    if (sources.length !== 3 || proposed.length !== 3) throw new Error("規劃器未恰好選取三扇來源窗戶。");
    windowReplacementPreview = {
      planId: String(result.PlanId), roomId: idKey(room.RoomId), selectedIds: sources.map((row) => idKey(row.WindowId)),
      targetType: `${textValue(object(result.TargetType).Family)}: ${textValue(object(result.TargetType).TypeName)}`,
      sources, proposed, total: numberValue(result.TotalEffectiveArea) ?? 0, current: numberValue(result.CurrentTotalEffectiveArea) ?? 0,
      required: numberValue(result.RequiredArea) ?? 0, deficit: numberValue(result.Deficit) ?? 0,
      result: textValue(result.PredictedResult), safe: result.ReplacementSafe === true,
      warnings: Array.isArray(result.ReplacementWarnings) ? result.ReplacementWarnings.map(String) : [],
      blockingWarnings: Array.isArray(result.BlockingWarnings) ? result.BlockingWarnings.map(String) : [],
      informationalWarnings: Array.isArray(result.InformationalWarnings) ? result.InformationalWarnings.map(String) : [], canApply: result.CanApply === true,
    };
    const viewer = roomViewer;
    if (!viewer || viewer.roomId !== idKey(room.RoomId)) throw new Error("The selected Room 3D viewer is not ready.");
    addWindowRetrofitPreviewMeshes(viewer, sources, proposed);
  } catch (error) { windowReplacementPreview = null; windowPlanError = error instanceof Error ? error.message : String(error); }
  finally { windowPlanBusy = false; render(); }
}

async function applyWindowReplacementPreview(): Promise<void> {
  const preview = windowReplacementPreview;
  if (!preview?.canApply || windowPlanBusy) return;
  windowPlanBusy = true; windowPlanError = ""; render();
  try {
    const result = object(await callAllowedTool("apply_smoke_exhaust_window_replacement", { planId: preview.planId }));
    if (result.Success !== true) throw new Error("Revit 未確認固定窗替換作業。");
    replacementOperationId = textValue(result.OperationId); replacementOperationRoomId = idKey(result.RoomId ?? preview.roomId);
    if (!replacementOperationId) throw new Error("MCP 未回傳供復原使用的作業識別碼。");
    windowReplacementPreview = null; clearWindowPlanPreviewMeshes(); await refreshSelectedRoomData(preview.roomId);
  } catch (error) { windowPlanError = error instanceof Error ? error.message : String(error); }
  finally { windowPlanBusy = false; render(); }
}

async function undoWindowReplacementOperation(): Promise<void> {
  if (!replacementOperationId || windowPlanBusy) return;
  const roomId = replacementOperationRoomId ?? ""; windowPlanBusy = true; windowPlanError = ""; render();
  try {
    const result = object(await callAllowedTool("undo_smoke_exhaust_window_replacement", { operationId: replacementOperationId }));
    if (result.Success !== true) throw new Error("Revit 未確認替換復原作業。");
    replacementOperationId = null; replacementOperationRoomId = null;
    if (roomId) await refreshSelectedRoomData(roomId);
  } catch (error) { windowPlanError = error instanceof Error ? error.message : String(error); }
  finally { windowPlanBusy = false; render(); }
}

async function previewWindowRetrofit(room: CheckedRoom): Promise<void> {
  if (windowPlanBusy || textValue(object(room.Summary).Result) !== "FAIL") return;
  const targetWindowIds = getRetrofitTargetWindowIds(room);
  if (targetWindowIds.length !== 2) {
    windowPlanError = "找不到位於同一面宿主牆、屬於此房間的兩扇有效既有窗戶。"; render(); return;
  }
  windowPlanBusy = true; windowPlanError = ""; windowPlanMessage = "";
  windowRetrofitPreview = null; clearWindowPlanPreviewMeshes(); render();
  try {
    const result = object(await callAllowedTool("plan_smoke_exhaust_window_retrofit", {
      roomId: Number(room.RoomId), targetWindowIds,
      targetWindowType: "Window-Sliding-Double: 56\" x 60\"",
      targetWidthMm: 1422.4, targetHeightMm: 1524, targetSillHeightMm: 1590,
    }));
    if (result.Success !== true || !result.PlanId) throw new Error(textValue(result.Error, "MCP 未回傳窗戶改善方案。"));
    const originalWindows = Array.isArray(result.OriginalWindows) ? result.OriginalWindows.map(object) : [];
    const proposedWindows = Array.isArray(result.ProposedWindows) ? result.ProposedWindows.map(object) : [];
    if (originalWindows.length !== 2 || proposedWindows.length !== 2) throw new Error("MCP 未回傳兩扇原窗及兩扇改善窗的資料快照。");
    windowRetrofitPreview = {
      planId: String(result.PlanId), roomId: idKey(room.RoomId), windowType: textValue(result.WindowType),
      originalWindows, proposedWindows,
      currentTotalEffectiveArea: numberValue(result.CurrentTotalEffectiveArea) ?? 0,
      proposedTotalEffectiveArea: numberValue(result.ProposedTotalEffectiveArea) ?? 0,
      proposedTotalEffectiveAreaExact: numberValue(result.ProposedExactTotalEffectiveArea) ?? 0,
      requiredArea: numberValue(result.RequiredArea) ?? 0, deficit: numberValue(result.Deficit) ?? 0,
      predictedResult: textValue(result.PredictedResult), ceilingHeightMm: numberValue(result.CeilingHeightMm) ?? 0,
      headWithinCeiling: result.HeadWithinCeiling === true, canApply: result.CanApply === true,
    };
    const viewer = roomViewer;
    if (!viewer || viewer.roomId !== idKey(room.RoomId)) throw new Error("所選房間的 3D 檢視器尚未就緒。");
    if (!viewer.boundarySegments.length) {
      const info = object(await callAllowedTool("get_room_info", { roomId: Number(room.RoomId) }));
      viewer.boundarySegments = Array.isArray(info.BoundarySegments) ? info.BoundarySegments as RoomBoundarySegment[] : [];
    }
    addWindowRetrofitPreviewMeshes(viewer, originalWindows, proposedWindows);
  } catch (error) {
    windowRetrofitPreview = null; clearWindowPlanPreviewMeshes();
    windowPlanError = error instanceof Error ? error.message : String(error);
  } finally {
    windowPlanBusy = false; render();
  }
}

function addWindowRetrofitPreviewMeshes(viewer: RoomViewer, originals: Dict[], proposed: Dict[]): void {
  clearWindowPlanPreviewMeshes();
  const proposedById = new Map(proposed.map((win) => [idKey(win.WindowId), win]));
  for (const original of originals) {
    const id = idKey(original.WindowId);
    const originalMesh = viewer.windowMeshes.get(id);
    if (originalMesh) {
      (originalMesh.material as THREE.MeshBasicMaterial).opacity = 0.16;
      viewer.fadedWindowIds.add(id);
    }
    const next = proposedById.get(id);
    if (!next) continue;
    const locationX = numberValue(next.LocationX), locationY = numberValue(next.LocationY);
    const widthMm = numberValue(next.WidthMm), heightMm = numberValue(next.HeightMm), sillMm = numberValue(next.SillHeightMm);
    if (locationX == null || locationY == null || widthMm == null || heightMm == null || sillMm == null) continue;
    const nearest = viewer.boundarySegments.reduce<{ segment: RoomBoundarySegment | null; distance: number }>((best, segment) => {
      const dx = segment.EndX - segment.StartX, dy = segment.EndY - segment.StartY, len2 = dx * dx + dy * dy;
      if (!len2) return best;
      const t = THREE.MathUtils.clamp(((locationX - segment.StartX) * dx + (locationY - segment.StartY) * dy) / len2, 0, 1);
      const distance = Math.hypot(locationX - (segment.StartX + t * dx), locationY - (segment.StartY + t * dy));
      return distance < best.distance ? { segment, distance } : best;
    }, { segment: null, distance: Infinity });
    if (!nearest.segment) continue;
    const s = nearest.segment;
    const sx = (s.StartX - viewer.originX) / 1000, sz = (viewer.originY - s.StartY) / 1000;
    const ex = (s.EndX - viewer.originX) / 1000, ez = (viewer.originY - s.EndY) / 1000;
    const dx = ex - sx, dz = ez - sz, angle = Math.atan2(-dz, dx);
    const outward = new THREE.Vector2(-dz, dx).normalize();
    if (outward.x * ((sx + ex) / 2) + outward.y * ((sz + ez) / 2) < 0) outward.negate();
    const x = (locationX - viewer.originX) / 1000, z = (viewer.originY - locationY) / 1000;
    const geometry = new THREE.PlaneGeometry(widthMm / 1000, heightMm / 1000);
    const mesh = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({ color: 0xc36cff, side: THREE.DoubleSide, transparent: true, opacity: 0.58, depthWrite: false }));
    mesh.position.set(x + outward.x * (WALL_THICKNESS_MM / 2000 + 0.008), (sillMm + heightMm / 2) / 1000, z + outward.y * (WALL_THICKNESS_MM / 2000 + 0.008));
    mesh.rotation.y = angle; mesh.renderOrder = 8; mesh.userData.previewOnly = true; mesh.userData.retrofitWindowId = id;
    const outline = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({ color: 0xf0d5ff }));
    outline.position.copy(mesh.position); outline.rotation.copy(mesh.rotation); outline.renderOrder = 9;
    viewer.roomGroup.add(mesh, outline); viewer.previewObjects.push(mesh, outline);
  }
}

function addWindowPlanPreviewMeshes(viewer: RoomViewer, placements: PlannedPlacement[]): void {
  clearWindowPlanPreviewMeshes();
  for (const placement of placements) {
    const hostSegments = viewer.boundarySegments.filter((segment) => {
      const id = segment.HostWallId ?? segment.WallId ?? segment.ElementId;
      return id != null && String(id) === String(placement.hostWallId);
    });
    const candidateSegments = hostSegments.length ? hostSegments : viewer.boundarySegments;
    const nearest = candidateSegments.reduce<{ segment: RoomBoundarySegment | null; distance: number; x: number; y: number }>((best, segment) => {
      const dx = segment.EndX - segment.StartX, dy = segment.EndY - segment.StartY, len2 = dx * dx + dy * dy;
      if (!len2) return best;
      const t = THREE.MathUtils.clamp(((placement.locationX - segment.StartX) * dx + (placement.locationY - segment.StartY) * dy) / len2, 0, 1);
      const x = segment.StartX + t * dx, y = segment.StartY + t * dy;
      const distance = Math.hypot(placement.locationX - x, placement.locationY - y);
      return distance < best.distance ? { segment, distance, x, y } : best;
    }, { segment: null, distance: Infinity, x: placement.locationX, y: placement.locationY });
    if (!nearest.segment) continue;
    const sx = (nearest.segment.StartX - viewer.originX) / 1000, sz = (viewer.originY - nearest.segment.StartY) / 1000;
    const ex = (nearest.segment.EndX - viewer.originX) / 1000, ez = (viewer.originY - nearest.segment.EndY) / 1000;
    const dx = ex - sx, dz = ez - sz, angle = Math.atan2(-dz, dx);
    const outward = new THREE.Vector2(-dz, dx).normalize();
    const midX = (sx + ex) / 2, midZ = (sz + ez) / 2;
    if (outward.x * midX + outward.y * midZ < 0) outward.negate();
    const x = (placement.locationX - viewer.originX) / 1000;
    const z = (viewer.originY - placement.locationY) / 1000;
    const geometry = new THREE.PlaneGeometry(placement.widthMm / 1000, placement.heightMm / 1000);
    const diagnostic = placement.isDiagnostic === true;
    const mesh = new THREE.Mesh(geometry, new THREE.MeshBasicMaterial({
      color: diagnostic ? 0xe5485d : 0xc36cff, side: THREE.DoubleSide, transparent: true,
      opacity: diagnostic ? 0.5 : 0.58, depthWrite: false,
    }));
    mesh.position.set(x + outward.x * (WALL_THICKNESS_MM / 2000 + 0.008), (placement.sillHeightMm + placement.heightMm / 2) / 1000,
      z + outward.y * (WALL_THICKNESS_MM / 2000 + 0.008));
    mesh.rotation.y = angle; mesh.renderOrder = 8; mesh.userData.previewOnly = true;
    const outline = new THREE.LineSegments(new THREE.EdgesGeometry(geometry), new THREE.LineBasicMaterial({
      color: diagnostic ? 0xff5368 : 0xf0d5ff, transparent: true, opacity: 1,
    }));
    outline.position.copy(mesh.position); outline.rotation.copy(mesh.rotation); outline.renderOrder = 9;
    viewer.roomGroup.add(mesh, outline); viewer.previewObjects.push(mesh, outline);
  }
}

function clearWindowPlanPreviewMeshes(): void {
  const viewer = roomViewer;
  if (!viewer) return;
  for (const object of viewer.previewObjects) {
    viewer.roomGroup.remove(object);
    object.traverse((child) => {
      if (!(child instanceof THREE.Mesh || child instanceof THREE.LineSegments)) return;
      child.geometry.dispose();
      const materials = Array.isArray(child.material) ? child.material : [child.material];
      materials.forEach((material) => material.dispose());
    });
  }
  viewer.previewObjects = [];
  for (const id of viewer.fadedWindowIds) {
    const original = viewer.windowMeshes.get(id);
    if (original) {
      const material = original.material as THREE.MeshBasicMaterial;
      material.opacity = 0.98; material.transparent = true; material.needsUpdate = true;
    }
  }
  viewer.fadedWindowIds.clear();
}

function cancelWindowPlanPreview(): void {
  windowPlanPreview = null; windowPlanError = ""; windowPlanMessage = "";
  clearWindowPlanPreviewMeshes(); render();
}

function cancelWindowRetrofitPreview(): void {
  windowRetrofitPreview = null; windowPlanError = ""; windowPlanMessage = "";
  clearWindowPlanPreviewMeshes(); render();
}

async function applyWindowPlanPreview(): Promise<void> {
  const preview = windowPlanPreview;
  if (!preview || windowPlanBusy || !preview.canApply || !preview.placementSufficient ||
      preview.foundPositions < preview.requestedCount || preview.placements.length < preview.requestedCount || !preview.planId) return;
  windowPlanBusy = true; windowPlanError = ""; render();
  try {
    const result = object(await callAllowedTool("apply_smoke_exhaust_window_plan", { planId: preview.planId }));
    windowOperationId = typeof result.OperationId === "string" ? result.OperationId : String(result.operationId ?? "");
    windowOperationRoomId = idKey(result.RoomId ?? result.roomId ?? preview.roomId);
    if (!windowOperationId) throw new Error("MCP 未回傳供復原使用的作業識別碼。");
    windowPlanPreview = null; clearWindowPlanPreviewMeshes();
    await refreshSelectedRoomData(preview.roomId);
    const refreshed = checkedRooms.get(preview.roomId);
    windowPlanMessage = `套用完成；實際檢核結果：${textValue(object(refreshed?.Summary).Result, "未取得")}`;
  } catch (error) {
    windowPlanError = error instanceof Error ? error.message : String(error);
  } finally {
    windowPlanBusy = false; render();
  }
}

async function undoWindowPlanOperation(): Promise<void> {
  if (!windowOperationId || windowPlanBusy) return;
  const operationId = windowOperationId;
  const roomId = windowOperationRoomId ?? (selectedRoom ? idKey(selectedRoom.ElementId) : "");
  windowPlanBusy = true; windowPlanError = ""; render();
  try {
    const result = object(await callAllowedTool("undo_smoke_exhaust_window_plan", { operationId }));
    if (result.Success !== true) throw new Error("Revit 未確認方案復原作業。");
    windowOperationId = null; clearWindowPlanPreviewMeshes();
    windowOperationRoomId = null;
    if (roomId) await refreshSelectedRoomData(roomId);
    const refreshed = roomId ? checkedRooms.get(roomId) : null;
    windowPlanMessage = `已復原；實際檢核結果：${textValue(object(refreshed?.Summary).Result, "未取得")}`;
  } catch (error) {
    windowPlanError = error instanceof Error ? error.message : String(error);
  } finally {
    windowPlanBusy = false; render();
  }
}

async function applyWindowRetrofitPreview(): Promise<void> {
  const preview = windowRetrofitPreview;
  if (!preview || !preview.canApply || windowPlanBusy) return;
  windowPlanBusy = true; windowPlanError = ""; render();
  try {
    const result = object(await callAllowedTool("apply_smoke_exhaust_window_retrofit", { planId: preview.planId }));
    if (result.Success !== true) throw new Error("Revit 未確認既有窗改善作業。");
    retrofitOperationId = typeof result.OperationId === "string" ? result.OperationId : "";
    retrofitOperationRoomId = idKey(result.RoomId ?? preview.roomId);
    if (!retrofitOperationId) throw new Error("MCP 未回傳供改善復原使用的作業識別碼。");
    windowRetrofitPreview = null; clearWindowPlanPreviewMeshes();
    await refreshSelectedRoomData(preview.roomId);
    const refreshed = checkedRooms.get(preview.roomId);
    windowPlanMessage = `既有窗已改善並重新檢核：${textValue(object(refreshed?.Summary).Result)}`;
  } catch (error) {
    windowPlanError = error instanceof Error ? error.message : String(error);
  } finally {
    windowPlanBusy = false; render();
  }
}

async function undoWindowRetrofitOperation(): Promise<void> {
  if (!retrofitOperationId || windowPlanBusy) return;
  const operationId = retrofitOperationId;
  const roomId = retrofitOperationRoomId ?? (selectedRoom ? idKey(selectedRoom.ElementId) : "");
  windowPlanBusy = true; windowPlanError = ""; render();
  try {
    const result = object(await callAllowedTool("undo_smoke_exhaust_window_retrofit", { operationId }));
    if (result.Success !== true || textValue(object(result.Recheck).Result) !== "FAIL")
      throw new Error("改善復原後未確認原檢核 FAIL 狀態已恢復。");
    retrofitOperationId = null; retrofitOperationRoomId = null;
    if (roomId) await refreshSelectedRoomData(roomId);
    windowPlanInsufficient = true;
    windowPlanMessage = result.CreatedTypeRetainedInUse === true
      ? "已還原原類型與窗台高度；新類型仍被其他元素使用，因此予以保留。"
      : result.CreatedTypeDeleted === true
        ? "已還原原類型與窗台高度，並刪除本次建立且已無元素使用的新類型。"
        : "已還原原類型與窗台高度；房間重新檢核恢復 FAIL。";
  } catch (error) {
    windowPlanError = error instanceof Error ? error.message : String(error);
  } finally {
    windowPlanBusy = false; render();
  }
}

async function refreshSelectedRoomData(roomId: string): Promise<void> {
  const levelName = selectedLevel;
  if (!levelName) return;
  busy = true; render();
  try {
    const [roomData, checkData] = await Promise.all([
      callAllowedTool("get_rooms_by_level", { level: levelName, includeUnnamed: true }),
      callAllowedTool("check_smoke_exhaust_windows", { levelName, colorize: false }),
    ]);
    rooms = arrayAt(roomData, "Rooms") as RoomRow[];
    checkedRooms = new Map(arrayAt(checkData, "Rooms").map((row) => [idKey(row.RoomId), row as CheckedRoom]));
    selectedRoom = rooms.find((item) => idKey(item.ElementId) === roomId) ?? null;
    selectedWindow = null;
    if (roomViewer) roomViewer.roomId = "";
  } finally {
    busy = false; render();
  }
}

function renderWindowDetails(room: CheckedRoom, win: WindowRow): void {
  const panel = el("section", "panel detail-panel");
  panel.append(el("div", "section-heading", `單窗詳細資訊 · 窗戶 ${textValue(win.WindowId)}`));
  const diagnosisText = diagnosis(win);
  const details: Array<[string, unknown, string]> = [
    ["窗戶 ID", win.WindowId, ""], ["族群", win.FamilyName, ""], ["類型", win.TypeName, ""],
    ["窗寬", win.Width, " mm"], ["窗高", win.Height, " mm"], ["窗台高度", win.SillHeight, " mm"],
    ["窗頂高度", win.HeadHeight, " mm"], ["有效帶內高度", win.HeightInZone, " mm"],
    ["有效帶內面積", win.AreaInZone, " m²"], ["開啟比例", win.OpeningRatio, ""],
    ["有效開口面積", win.EffectiveArea, " m²"], ["診斷結果", diagnosisText, ""],
  ];
  const detailGrid = el("dl", "detail-grid");
  for (const [label, value, unit] of details) {
    const item = el("div", "detail-item");
    item.append(el("dt", "", label));
    const shown = label === "開啟比例" ? fmtRatio(value) : typeof value === "number" ? fmt(value, ["窗寬", "窗高", "窗台高度", "窗頂高度", "有效帶內高度"].includes(label) ? 1 : 4) : textValue(value);
    const dd = el("dd", label === "診斷結果" ? diagnosisClass(diagnosisText) : "", `${shown}${value == null || label === "開啟比例" ? "" : unit}`);
    item.append(dd);
    detailGrid.append(item);
  }
  panel.append(detailGrid);

  const ratio = numberValue(win.OpeningRatio);
  const areaInZone = numberValue(win.AreaInZone);
  const effectiveArea = numberValue(win.EffectiveArea);
  const summary = object(room.Summary);
  const roomTotalEffectiveArea = numberValue(summary.TotalEffectiveArea);
  const roomDeficit = numberValue(summary.Deficit);
  const roomResult = textValue(summary.Result, "未檢核");
  const scenarios = object(win.Scenarios);
  const fiftyPercent = object(scenarios.FiftyPercent);
  const hundredPercent = object(scenarios.HundredPercent);
  const scenario = el("div", "scenario");
  scenario.append(el("h3", "", "選取窗戶改善情境"));
  const hasScenarioData = Object.keys(fiftyPercent).length > 0 && Object.keys(hundredPercent).length > 0;
  if (hasScenarioData) {
    scenario.append(el("p", "scenario-note", "比較調整此窗後，對整個房間檢核結果的影響。"));
    const cards = el("div", "scenario-cards");
    cards.append(scenarioCard("現況", [
      ["目前開啟比例", fmtRatio(ratio)], ["目前此窗有效開口面積", `${fmt(effectiveArea)} m²`],
      ["目前房間總有效開口面積", `${fmt(roomTotalEffectiveArea)} m²`], ["目前不足面積", `${fmt(roomDeficit)} m²`],
      ["目前房間檢核結果", roomResult, statusClass(roomResult)],
    ]));
    cards.append(scenarioCard("50% 假設", [
      ["開啟比例", fmtRatio(fiftyPercent.OpeningRatio)], ["此窗有效開口面積", `${fmt(fiftyPercent.EffectiveArea)} m²`],
      ["增加有效面積", `+${fmt(fiftyPercent.Increase)} m²`], ["房間總有效開口面積", `${fmt(fiftyPercent.TotalEffectiveArea)} m²`],
      ["改善後不足面積", `${fmt(fiftyPercent.Deficit)} m²`], ["改善後房間檢核結果", textValue(fiftyPercent.Result), statusClass(textValue(fiftyPercent.Result))],
    ]));
    cards.append(scenarioCard("100% 假設", [
      ["開啟比例", fmtRatio(hundredPercent.OpeningRatio)], ["此窗有效開口面積", `${fmt(hundredPercent.EffectiveArea)} m²`],
      ["增加有效面積", `+${fmt(hundredPercent.Increase)} m²`], ["房間總有效開口面積", `${fmt(hundredPercent.TotalEffectiveArea)} m²`],
      ["改善後不足面積", `${fmt(hundredPercent.Deficit)} m²`], ["改善後房間檢核結果", textValue(hundredPercent.Result), statusClass(textValue(hundredPercent.Result))],
    ]));
    scenario.append(cards);
    const fiftyResult = textValue(fiftyPercent.Result);
    const hundredResult = textValue(hundredPercent.Result);
    const conclusionText = fiftyResult === "PASS"
      ? "此窗假設調整至 50% 開啟比例後，即可滿足目前檢核需求。"
      : hundredResult === "PASS"
        ? "50% 仍不足；假設調整至 100% 後可滿足目前檢核需求。"
        : `即使此窗假設為 100% 開啟，仍不足 ${fmt(hundredPercent.Deficit)} m²，需再檢討其他有效開口或新增開口。`;
    const conclusion = el("div", "scenario-conclusion");
    conclusion.append(el("strong", "", "方案結論"), el("p", "", conclusionText));
    scenario.append(conclusion);
  } else {
    const info = el("div", "scenario-info-card");
    if (win.IsInSmokeZone === false || (numberValue(win.HeightInZone) != null && numberValue(win.HeightInZone)! <= 0)) {
      info.append(
        el("p", "", "此窗未進入有效排煙帶，即使調整開啟比例，也無法增加有效開口面積。"),
        el("p", "", "建議優先檢討窗戶高度或位置。"),
      );
    } else if (win.NeedsManualConfirm === true) {
      info.append(el("p", "", "此窗的開啟型式目前無法自動判定，需先人工確認後才能進行情境比較。"));
    } else {
      info.append(el("p", "", "目前無可用的 50% / 100% 改善情境資料。"));
    }
    scenario.append(info);
  }
  panel.append(scenario);
  const windowId = validWindowId(win.WindowId);
  const heading = panel.firstElementChild as HTMLElement | null;
  if (heading && windowId != null) {
    heading.classList.add("detail-heading");
    const locateButton = el("button", "locate-button", locatingWindowId === windowId ? "定位中…" : "在 Revit 中定位") as HTMLButtonElement;
    locateButton.type = "button";
    locateButton.disabled = locatingWindowId != null;
    locateButton.addEventListener("click", () => { void locateWindow(windowId); });
    heading.append(locateButton);
  }
  if (windowId != null && locationStatus?.windowId === windowId) {
    const status = el("p", `location-status${locationStatus.success ? " success" : " failure"}`,
      locationStatus.success ? `已在 Revit 中定位窗戶 ${windowId}` : "無法在 Revit 中定位此窗");
    panel.insertBefore(status, panel.children[1] ?? null);
    if (!locationStatus.success && locationStatus.technicalMessage) {
      const technical = el("p", "location-technical", locationStatus.technicalMessage);
      panel.insertBefore(technical, panel.children[2] ?? null);
    }
  }
  root.append(panel);
}

async function locateWindow(windowId: number): Promise<void> {
  const id = validWindowId(windowId);
  if (id == null || locatingWindowId != null) return;
  locatingWindowId = id;
  locationStatus = null;
  render();
  try {
    await callAllowedTool("select_element", { elementId: id });
    await callAllowedTool("zoom_to_element", { elementId: id });
    locationStatus = { windowId: id, success: true };
  } catch (error) {
    const technicalMessage = error instanceof Error ? error.message : String(error);
    console.error(`無法在 Revit 中定位窗戶 ${id}`, error);
    locationStatus = { windowId: id, success: false, technicalMessage };
  } finally {
    locatingWindowId = null;
    render();
  }
}

function scenarioCard(title: string, rows: Array<[string, string, string?]>): HTMLElement {
  const card = el("div", "scenario-card");
  card.append(el("h4", "", title));
  for (const [label, value, valueClass] of rows) {
    const item = el("div", "scenario-value");
    item.append(el("span", "", label), el("strong", valueClass ? `scenario-status ${valueClass}` : "", value));
    card.append(item);
  }
  return card;
}

async function loadLevel(levelName: string): Promise<void> {
  selectedLevel = levelName;
  rooms = [];
  checkedRooms = new Map();
  selectedRoom = null;
  selectedWindow = null;
  windowPlanPreview = null; windowPlanError = ""; windowPlanMessage = "";
  windowRetrofitPreview = null; windowPlanInsufficient = false;
  clearWindowPlanPreviewMeshes();
  errorText = "";
  if (!levelName) { render(); return; }
  busy = true;
  render();
  try {
    const [roomData, checkData] = await Promise.all([
      callAllowedTool("get_rooms_by_level", { level: levelName, includeUnnamed: true }),
      callAllowedTool("check_smoke_exhaust_windows", { levelName, colorize: false }),
    ]);
    rooms = arrayAt(roomData, "Rooms") as RoomRow[];
    checkedRooms = new Map(arrayAt(checkData, "Rooms").map((row) => [idKey(row.RoomId), row as CheckedRoom]));
  } catch (error) {
    errorText = error instanceof Error ? error.message : String(error);
  } finally {
    busy = false;
    render();
  }
}

app.ontoolresult = (params) => {
  try {
    const result = parseResult(params);
    levels = arrayAt(result, "Levels").filter((row) => typeof row.Name === "string") as LevelRow[];
    errorText = levels.length ? "" : "MCP 未回傳可用的樓層清單。";
    render();
  } catch (error) {
    errorText = error instanceof Error ? error.message : String(error);
    render();
  }
};

if (window.parent === window) {
  void callAllowedTool("get_all_levels", {}).then((result) => {
    levels = arrayAt(result, "Levels").filter((row) => typeof row.Name === "string") as LevelRow[];
    errorText = levels.length ? "" : "MCP 未回傳可用的樓層清單。";
    render();
  }).catch((error: unknown) => {
    errorText = error instanceof Error ? error.message : String(error);
    render();
  });
} else {
  app.connect().catch((error: unknown) => {
    root.replaceChildren(el("div", "error-message", `MCP App 主機連線失敗：${String((error as { message?: string })?.message ?? error)}`));
  });
}
