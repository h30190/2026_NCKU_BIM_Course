/**
 * 房間避難路徑法規檢核與圖紙排版工具 (Egress Audit & Sheet Note Tools)
 */

import { z } from "zod";
import { Tool } from "@modelcontextprotocol/sdk/types.js";
import * as http from "http";
import * as fs from "fs";
import * as os from "os";
import * as path from "path";
import { execFile } from "child_process";
import { RevitSocketClient } from "../socket.js";

/**
 * Zod 輸入驗證 Schema：audit_room_egress
 */
export const auditRoomEgressInputSchema = z.object({
    roomIds: z
        .array(z.number())
        .min(1, "roomIds 至少需包含一個房間 ElementId")
        .describe("欲檢核的房間 ElementId 清單"),
    exitDoorIds: z
        .array(z.number())
        .min(1, "exitDoorIds 至少需包含一個避難出口門 ElementId")
        .describe("作為避難出口的門 ElementId 清單（可傳入多個候選出口；每個房間會自動擇定最近出口，僅繪製 1 條動線）"),
    maxAllowedMeters: z
        .number()
        .positive("maxAllowedMeters 必須大於 0")
        .describe("法規容許最大步行距離公尺數"),
    dimensionSegments: z
        .boolean()
        .optional()
        .default(true)
        .describe("是否自動於逃生路徑每個轉折線段（>= 10cm）上建立 Revit 原生線性尺寸標註（預設為 true）"),
    occupancyCategory: z
        .string()
        .optional()
        .describe("法規適用類組與構造說明文字（例如：'B-2 辦公場所（第 93 條第 3 款｜主要構造：防火構造）'）"),
    openReport: z
        .boolean()
        .optional()
        .default(true)
        .describe("檢核完成後是否自動於系統預設瀏覽器開啟外部視覺化總結報告（支援點擊空間直接連動 Revit，預設為 true）"),
});

export type AuditRoomEgressInput = z.infer<typeof auditRoomEgressInputSchema>;

/**
 * Zod 輸入驗證 Schema：generate_egress_sheet_note
 */
export const generateEgressSheetNoteInputSchema = z.object({
    sheetNumber: z
        .string()
        .min(1, "sheetNumber 不可為空")
        .describe('要放置法規檢討備註文字的目標圖號，例如 "A-101"'),
    regulationNoteText: z
        .string()
        .describe("AI 根據建築技術規則自動彙整的法規說明與結論文字，支援換行符號"),
    placeScheduleOnSheet: z
        .boolean()
        .optional()
        .default(false)
        .describe("是否將「避難步行距離檢討表」直接排版放置到圖紙上（預設為 false：明細表僅建立於 Revit 專案明細表清單中，不直接排版到檢討樓層的圖紙上）"),
});

export type GenerateEgressSheetNoteInput = z.infer<typeof generateEgressSheetNoteInputSchema>;

export const egressAuditTools: Tool[] = [
    {
        name: "audit_room_egress",
        description:
            "執行房間避難步行距離法規檢核。自動建立房間避難共用參數、由房間外圈頂點內縮 0.3m 計算至各避難出口門的室內逃生路徑，多出口時自動為每個房間擇定最近出口並僅保留 1 條最不利路徑、寫入合格狀態參數、隨距離檢討自動於 Revit 專案中建立或更新「避難步行距離檢討表」明細表（ViewSchedule，僅存於專案明細表清單、不直接排版至圖紙上）、套用視圖紅綠線型覆寫、中點標籤、於每個轉折線段（>= 10cm）建立 Revit 原生線性尺寸標註，並自動於預設瀏覽器彈出外部視覺化總結報告（支援點選空間直接連動顯示 Revit 內該空間與逃生路徑）。",
        inputSchema: {
            type: "object",
            properties: {
                roomIds: {
                    type: "array",
                    items: { type: "number" },
                    description: "欲檢核的房間 ElementId 清單",
                },
                exitDoorIds: {
                    type: "array",
                    items: { type: "number" },
                    description: "作為避難出口的門 ElementId 清單（可傳入多個候選出口；每個房間會自動擇定最近出口，僅繪製 1 條動線）",
                },
                maxAllowedMeters: {
                    type: "number",
                    description: "法規容許最大步行距離公尺數",
                },
                dimensionSegments: {
                    type: "boolean",
                    description:
                        "是否自動於逃生路徑每個轉折線段（>= 10cm）上建立 Revit 原生線性尺寸標註（預設 true）",
                },
                occupancyCategory: {
                    type: "string",
                    description:
                        "法規適用類組與構造說明文字（例如：'B-2 辦公場所（第 93 條第 3 款｜主要構造：防火構造）'）",
                },
                openReport: {
                    type: "boolean",
                    description:
                        "檢核完成後是否自動於系統預設瀏覽器彈出外部視覺化總結報告（預設 true）",
                },
            },
            required: ["roomIds", "exitDoorIds", "maxAllowedMeters"],
        },
    },
    {
        name: "generate_egress_sheet_note",
        description:
            "建立或更新 Revit 專案中的「避難步行距離檢討表」明細表（預設僅存於 Revit 明細表清單、不直接排版到檢討樓層的圖紙上），並於指定圖紙放置法規檢討綜整說明文字（TextNote）。",
        inputSchema: {
            type: "object",
            properties: {
                sheetNumber: {
                    type: "string",
                    description: '要放置法規檢討說明備註的目標圖號，例如 "A-101"',
                },
                regulationNoteText: {
                    type: "string",
                    description: "AI 根據建築技術規則自動彙整的法規說明與結論文字，支援換行符號",
                },
                placeScheduleOnSheet: {
                    type: "boolean",
                    description:
                        "是否將「避難步行距離檢討表」直接排版放置到圖紙上（預設 false，不放置於圖紙上，僅建立為 Revit 明細表視圖）",
                },
            },
            required: ["sheetNumber", "regulationNoteText"],
        },
    },
];

// ============================================================================
// 本機輕量 HTTP 橋接伺服器（讓外部瀏覽器報告可透過既有 MCP-Server 連線連動 Revit）
// ============================================================================

let bridgeServer: http.Server | null = null;
let bridgePort: number | null = null;
let latestReportHtml = "";
let activeRevitClient: RevitSocketClient | null = null;

function readRequestBody(req: http.IncomingMessage): Promise<string> {
    return new Promise((resolve, reject) => {
        let body = "";
        req.on("data", (chunk) => {
            body += chunk.toString();
            if (body.length > 1_000_000) {
                reject(new Error("Payload too large"));
            }
        });
        req.on("end", () => resolve(body));
        req.on("error", reject);
    });
}

async function ensureEgressReportBridge(client: RevitSocketClient): Promise<number> {
    activeRevitClient = client;

    if (bridgeServer && bridgePort) {
        return bridgePort;
    }

    return new Promise<number>((resolve, reject) => {
        const server = http.createServer(async (req, res) => {
            // 允許從本機 file:// 或 http://127.0.0.1 呼叫本機橋接 API
            res.setHeader("Access-Control-Allow-Origin", "*");
            res.setHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
            res.setHeader("Access-Control-Allow-Headers", "Content-Type");

            if (req.method === "OPTIONS") {
                res.writeHead(204);
                res.end();
                return;
            }

            const url = req.url || "/";
            if (req.method === "GET" && (url === "/" || url.startsWith("/report"))) {
                res.writeHead(200, { "Content-Type": "text/html; charset=utf-8" });
                res.end(latestReportHtml || "<h1>尚無檢核報告資料</h1>");
                return;
            }

            if (req.method === "POST" && url === "/api/focus") {
                try {
                    if (!activeRevitClient) {
                        throw new Error("尚未連接 Revit Socket Client");
                    }
                    const raw = await readRequestBody(req);
                    const payload = JSON.parse(raw || "{}");
                    const roomId = Number(payload.roomId || 0);
                    const pathId = Number(payload.pathId || 0);
                    const segIds: number[] = Array.isArray(payload.segmentDimensionIds)
                        ? payload.segmentDimensionIds.map((x: any) => Number(x)).filter((x: number) => x > 0)
                        : [];

                    const selectIds = [roomId, pathId, ...segIds].filter((id) => id > 0);
                    if (selectIds.length > 0) {
                        await activeRevitClient.sendCommand("select_element", { elementIds: selectIds }, 15000);
                    }

                    // 優先縮放至逃生路徑元素（PathOfTravel 為平面視圖可見圖元，不會觸發 Room 的搜尋視圖模態對話框）
                    const zoomTargetId = pathId > 0 ? pathId : roomId;
                    if (zoomTargetId > 0) {
                        await activeRevitClient.sendCommand("zoom_to_element", { elementId: zoomTargetId }, 15000);
                    }

                    // 再次確保房間與路徑同時處於選取高亮狀態
                    if (selectIds.length > 0) {
                        await activeRevitClient.sendCommand("select_element", { elementIds: selectIds }, 15000);
                    }

                    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
                    res.end(JSON.stringify({ ok: true, selectedIds: selectIds, zoomedTo: zoomTargetId }));
                } catch (err: any) {
                    res.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
                    res.end(JSON.stringify({ ok: false, error: err?.message || String(err) }));
                }
                return;
            }

            if (req.method === "POST" && url === "/api/export-sheet") {
                try {
                    if (!activeRevitClient) {
                        throw new Error("尚未連接 Revit Socket Client");
                    }
                    const raw = await readRequestBody(req);
                    const payload = JSON.parse(raw || "{}");
                    const sheetNumber = String(payload.sheetNumber || "A-101").trim();
                    const regulationNoteText = String(payload.regulationNoteText || "");
                    const placeScheduleOnSheet = Boolean(payload.placeScheduleOnSheet === true);

                    const resp = await activeRevitClient.sendCommand(
                        "generate_egress_sheet_note",
                        { sheetNumber, regulationNoteText, placeScheduleOnSheet },
                        30000
                    );
                    if (!resp.success) {
                        throw new Error(resp.error || "輸出圖紙失敗");
                    }

                    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8" });
                    res.end(JSON.stringify({ ok: true, data: resp.data }));
                } catch (err: any) {
                    res.writeHead(500, { "Content-Type": "application/json; charset=utf-8" });
                    res.end(JSON.stringify({ ok: false, error: err?.message || String(err) }));
                }
                return;
            }

            res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8" });
            res.end("Not Found");
        });

        server.on("error", (err) => {
            reject(err);
        });

        // 僅綁定 127.0.0.1 確保本機安全
        server.listen(0, "127.0.0.1", () => {
            const addr = server.address();
            if (addr && typeof addr === "object") {
                bridgeServer = server;
                bridgePort = addr.port;
                resolve(addr.port);
            } else {
                reject(new Error("無法取得本機橋接埠號"));
            }
        });
    });
}

function openUrlInDefaultBrowser(targetUrl: string): void {
    try {
        if (process.platform === "win32") {
            // 當 MCP Server 由 AI 宿主於隔離/非互動式 WindowStation 啟動時，直接 start 會在隱藏桌面開啟視窗。
            // 透過 schtasks /it (Interactive) 委由 Windows 排程服務於使用者互動式桌面 (WinSta0\Default) 啟動 Edge 視窗。
            const cmdPath = path.join(os.tmpdir(), "open_egress_report.cmd");
            const edgeExe = "C:\\Program Files (x86)\\Microsoft\\Edge\\Application\\msedge.exe";
            const cmdContent = fs.existsSync(edgeExe)
                ? `@echo off\r\nstart "" "${edgeExe}" --new-window "${targetUrl}"\r\n`
                : `@echo off\r\nstart "" "${targetUrl}"\r\n`;
            fs.writeFileSync(cmdPath, cmdContent, "ascii");

            execFile(
                "cmd",
                [
                    "/c",
                    `schtasks /create /tn "RevitMCPOpenReport" /tr "${cmdPath}" /sc ONCE /st 00:00 /it /f && schtasks /run /tn "RevitMCPOpenReport" && timeout /t 2 /nobreak >nul && schtasks /delete /tn "RevitMCPOpenReport" /f`,
                ],
                (err) => {
                    if (err) {
                        execFile("cmd", ["/c", "start", "", targetUrl], () => {});
                    }
                }
            );
        } else if (process.platform === "darwin") {
            execFile("open", [targetUrl], () => {});
        } else {
            execFile("xdg-open", [targetUrl], () => {});
        }
    } catch {
        // 忽略自動開啟瀏覽器失敗
    }
}

/**
 * 根據檢核結果產生獨立外部視覺化總結報告 HTML
 */
export function buildEgressVisualReportHtml(data: any, port: number): string {
    const safeJson = JSON.stringify(data ?? {}).replace(/</g, "\\u003c");
    const generatedAt = new Date().toLocaleString("zh-TW", { hour12: false });

    return `<!DOCTYPE html>
<html lang="zh-Hant">
<head>
  <meta charset="UTF-8" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <title>建築技術規則第 93 條 — 避難步行距離檢核視覺化總結報告</title>
  <style>
    :root {
      --bg: #0f172a;
      --card: #1e293b;
      --content: #0f172a;
      --border: #334155;
      --fg: #f8fafc;
      --muted: #94a3b8;
      --primary: #3b82f6;
      --primary-fg: #ffffff;
    }
    @media (prefers-color-scheme: light) {
      :root {
        --bg: #f8fafc;
        --card: #ffffff;
        --content: #f1f5f9;
        --border: #e2e8f0;
        --fg: #0f172a;
        --muted: #64748b;
        --primary: #2563eb;
        --primary-fg: #ffffff;
      }
    }
    * { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      background: var(--bg);
      color: var(--fg);
      font-family: "Microsoft JhengHei", "Noto Sans TC", -apple-system, BlinkMacSystemFont, "Segoe UI", sans-serif;
      padding: 24px;
      line-height: 1.5;
    }
    .container { max-width: 1240px; margin: 0 auto; display: flex; flex-direction: column; gap: 20px; }
    .card {
      background: var(--card);
      border: 1px solid var(--border);
      border-radius: 12px;
      padding: 18px 20px;
      box-shadow: 0 2px 8px rgba(0, 0, 0, 0.08);
    }
    .header-bar {
      display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 16px;
    }
    .header-left { display: flex; align-items: center; gap: 14px; }
    .logo-box {
      width: 44px; height: 44px; border-radius: 12px;
      background: rgba(59, 130, 246, 0.15); border: 1px solid rgba(59, 130, 246, 0.35);
      display: flex; align-items: center; justify-content: center; font-size: 22px;
    }
    .title-row { display: flex; align-items: center; gap: 8px; flex-wrap: wrap; }
    .title-row h1 { font-size: 18px; font-weight: 700; }
    .pill-badge {
      font-size: 12px; padding: 2px 10px; border-radius: 999px;
      background: rgba(59, 130, 246, 0.15); color: #60a5fa; border: 1px solid rgba(59, 130, 246, 0.35); font-weight: 600;
    }
    .subtitle { font-size: 12px; color: var(--muted); margin-top: 3px; }
    .header-right { display: flex; align-items: center; gap: 10px; flex-wrap: wrap; }
    .conn-pill {
      display: inline-flex; align-items: center; gap: 8px;
      padding: 6px 12px; border-radius: 999px;
      background: rgba(16, 185, 129, 0.12); border: 1px solid rgba(16, 185, 129, 0.35);
      color: #10b981; font-size: 12px; font-weight: 600;
    }
    .conn-dot { width: 8px; height: 8px; border-radius: 50%; background: #10b981; }
    .btn-primary {
      padding: 7px 14px; border-radius: 8px; border: none;
      background: var(--primary); color: var(--primary-fg);
      font-size: 12px; font-weight: 600; cursor: pointer;
    }
    .btn-primary:hover { opacity: 0.92; }
    .section-hd { display: flex; align-items: center; justify-content: space-between; gap: 8px; flex-wrap: wrap; margin-bottom: 10px; }
    .section-hd-left { display: flex; align-items: center; gap: 8px; }
    .step-badge {
      font-size: 12px; font-weight: 700; padding: 2px 8px; border-radius: 6px;
      background: rgba(59, 130, 246, 0.15); color: #60a5fa; border: 1px solid rgba(59, 130, 246, 0.35);
    }
    .section-hd h2 { font-size: 14px; font-weight: 700; }
    .grid-4 { display: grid; grid-template-columns: repeat(auto-fit, minmax(250px, 1fr)); gap: 14px; }
    .cfg-card { display: flex; flex-direction: column; justify-content: space-between; min-height: 138px; }
    .cfg-title { font-size: 12px; font-weight: 600; color: var(--muted); display: flex; align-items: center; justify-content: space-between; }
    .cfg-main { font-size: 16px; font-weight: 700; margin-top: 6px; }
    .cfg-sub { font-size: 12px; color: var(--muted); margin-top: 4px; }
    .cfg-foot { margin-top: 12px; padding-top: 10px; border-top: 1px solid var(--border); font-size: 12px; color: var(--muted); display: flex; flex-wrap: wrap; gap: 6px; align-items: center; justify-content: space-between; }
    .chip-wrap { display: flex; flex-wrap: wrap; gap: 6px; margin-top: 8px; max-height: 86px; overflow-y: auto; }
    .chip {
      font-size: 12px; padding: 2px 8px; border-radius: 6px;
      background: var(--content); border: 1px solid var(--border); font-weight: 500;
    }
    .chip.fail-chip { background: rgba(239, 68, 68, 0.15); border-color: rgba(239, 68, 68, 0.35); color: #f87171; font-weight: 600; }
    .exit-item {
      font-size: 12px; padding: 6px 10px; border-radius: 8px;
      background: rgba(245, 158, 11, 0.12); border: 1px solid rgba(245, 158, 11, 0.35);
      color: #f59e0b; font-weight: 600; display: flex; align-items: center; justify-content: space-between; margin-top: 6px;
    }
    .kpi-val { font-size: 22px; font-weight: 800; margin-top: 4px; }
    .filter-group { display: flex; align-items: center; gap: 6px; }
    .filter-btn {
      padding: 5px 12px; border-radius: 999px; font-size: 12px; font-weight: 600;
      border: 1px solid var(--border); background: var(--card); color: var(--fg); cursor: pointer;
    }
    .filter-btn.active { background: var(--primary); color: var(--primary-fg); border-color: var(--primary); }
    .toast-bar {
      background: rgba(59, 130, 246, 0.1); border: 1px solid rgba(59, 130, 246, 0.3);
      border-radius: 12px; padding: 10px 16px; display: flex; flex-wrap: wrap; align-items: center; justify-content: space-between; gap: 8px; font-size: 12px;
      margin-bottom: 10px;
    }
    .code-tag {
      padding: 2px 8px; border-radius: 6px; background: var(--card); border: 1px solid var(--border);
      color: var(--muted); font-family: Consolas, monospace; font-size: 12px;
    }
    .table-card { padding: 0; overflow: hidden; }
    .table-wrap { overflow-x: auto; }
    table { width: 100%; border-collapse: collapse; text-align: left; font-size: 12px; }
    thead tr { background: var(--content); border-bottom: 1px solid var(--border); color: var(--muted); }
    th { padding: 12px 16px; font-weight: 700; }
    tbody tr { border-bottom: 1px solid var(--border); cursor: pointer; transition: background 0.15s; }
    tbody tr:hover { background: var(--content); }
    tbody tr.selected { background: rgba(59, 130, 246, 0.12); }
    td { padding: 14px 16px; vertical-align: middle; }
    .room-num-box {
      padding: 4px 8px; border-radius: 6px; font-weight: 700; font-size: 12px;
      background: var(--content); border: 1px solid var(--border); display: inline-block; min-width: 40px; text-align: center;
    }
    .room-num-box.active { background: var(--primary); color: #fff; border-color: var(--primary); }
    .progress-track {
      width: 100%; height: 8px; border-radius: 999px; background: var(--content); border: 1px solid var(--border); overflow: hidden;
    }
    .progress-bar { height: 100%; border-radius: 999px; }
    .status-pill {
      display: inline-flex; align-items: center; gap: 4px; padding: 4px 10px; border-radius: 999px; font-weight: 700; font-size: 12px;
    }
    .status-pass { background: rgba(16, 185, 129, 0.15); color: #10b981; border: 1px solid rgba(16, 185, 129, 0.35); }
    .status-fail { background: rgba(239, 68, 68, 0.15); color: #ef4444; border: 1px solid rgba(239, 68, 68, 0.35); }
    .link-btn {
      padding: 6px 12px; border-radius: 8px; font-size: 12px; font-weight: 600; cursor: pointer;
      border: 1px solid var(--border); background: var(--content); color: var(--fg);
    }
    .link-btn.active { background: var(--primary); color: #fff; border-color: var(--primary); }
  </style>
</head>
<body>
  <div class="container">
    <!-- 頂部標題列 -->
    <div class="card header-bar">
      <div class="header-left">
        <div class="logo-box">🏃</div>
        <div>
          <div class="title-row">
            <h1>建築技術規則第 93 條 — 避難步行距離檢核視覺化總結報告</h1>
            <span class="pill-badge">Revit 即時連動報告</span>
          </div>
          <div class="subtitle">
            產生時間：${generatedAt} ｜ 檢核引擎：<code>PathOfTravel.CreateMapped</code> + 轉折線段原生尺寸標註
          </div>
        </div>
      </div>
      <div class="header-right">
        <div class="conn-pill">
          <span class="conn-dot"></span>
          <span>Revit MCP 橋接中 (127.0.0.1:${port})</span>
        </div>
        <button class="btn-primary" id="sheetBtn" onclick="triggerSheetExport()">📝 輸出法規說明至圖紙（明細表僅存於專案）</button>
      </div>
    </div>

    <!-- STEP 1：設定條件與檢核基準 -->
    <div>
      <div class="section-hd">
        <div class="section-hd-left">
          <span class="step-badge">STEP 1</span>
          <h2>設定條件與檢核基準（檢討樓層 ／ 檢討房間名稱 ／ 檢討出口位置 ／ 法規適用類組）</h2>
        </div>
      </div>
      <div class="grid-4">
        <!-- 1. 檢討樓層 -->
        <div class="card cfg-card">
          <div>
            <div class="cfg-title"><span>🏢 1. 檢討樓層</span></div>
            <div class="cfg-main" id="cfg-level-name">—</div>
            <div class="cfg-sub" id="cfg-level-sub">—</div>
          </div>
          <div class="cfg-foot">
            <span class="chip">避難步行距離檢核</span>
            <span id="cfg-view-id">View ID: —</span>
          </div>
        </div>

        <!-- 2. 檢討房間名稱 -->
        <div class="card cfg-card">
          <div>
            <div class="cfg-title">
              <span>🚪 2. 檢討房間名稱</span>
              <span class="chip" id="cfg-room-count">共 0 間</span>
            </div>
            <div class="chip-wrap" id="cfg-room-chips"></div>
          </div>
          <div class="cfg-foot">
            <span>已排除非居室／未封閉空間</span>
          </div>
        </div>

        <!-- 3. 檢討出口位置 -->
        <div class="card cfg-card">
          <div>
            <div class="cfg-title">
              <span>🟢 3. 檢討出口位置</span>
              <span class="chip" id="cfg-exit-count">0 處出口</span>
            </div>
            <div id="cfg-exit-list"></div>
          </div>
          <div class="cfg-foot">
            <span>基準：房間外圈頂點內縮 30cm → 出口中心</span>
          </div>
        </div>

        <!-- 4. 法規適用類組 -->
        <div class="card cfg-card">
          <div>
            <div class="cfg-title"><span>⚖️ 4. 法規適用類組</span></div>
            <div class="cfg-main" id="cfg-occupancy" style="font-size:14px;">—</div>
            <div class="cfg-sub">依《建築技術規則》建築設計施工編第 93 條規定</div>
          </div>
          <div class="cfg-foot">
            <span>法定最大步行距離上限</span>
            <span class="pill-badge" id="cfg-max-allowed">L ≤ — m</span>
          </div>
        </div>
      </div>
    </div>

    <!-- KPI 統計摘要列 -->
    <div class="grid-4">
      <div class="card" style="display:flex;align-items:center;justify-content:space-between;">
        <div>
          <div class="cfg-title">檢核空間總數</div>
          <div class="kpi-val" id="kpi-total">0 間</div>
        </div>
        <span class="chip">100% 完成</span>
      </div>
      <div class="card" style="display:flex;align-items:center;justify-content:space-between;">
        <div>
          <div class="cfg-title">檢討通過 (合格)</div>
          <div class="kpi-val" style="color:#10b981;" id="kpi-pass">0 間</div>
        </div>
        <span class="status-pill status-pass" id="kpi-pass-rate">0% 合格</span>
      </div>
      <div class="card" style="display:flex;align-items:center;justify-content:space-between;">
        <div>
          <div class="cfg-title">檢討未通過 (超標)</div>
          <div class="kpi-val" style="color:#ef4444;" id="kpi-fail">0 間</div>
        </div>
        <span class="status-pill status-fail" id="kpi-fail-badge">0 間超標</span>
      </div>
      <div class="card" style="display:flex;align-items:center;justify-content:space-between;">
        <div>
          <div class="cfg-title">全層最不利路徑長度</div>
          <div class="kpi-val" id="kpi-max-dist">0.00 m</div>
        </div>
        <span class="chip" id="kpi-max-room">—</span>
      </div>
    </div>

    <!-- STEP 2：各空間最遠路徑分析結果 -->
    <div>
      <div class="section-hd">
        <div class="section-hd-left">
          <span class="step-badge">STEP 2</span>
          <h2>各空間最遠路徑分析結果（點選空間直接連動回 Revit 顯示該空間與逃生路徑）</h2>
        </div>
        <div class="filter-group">
          <button class="filter-btn active" id="btn-all" onclick="setFilter('all')">全部</button>
          <button class="filter-btn" id="btn-fail" onclick="setFilter('fail')">僅不合格</button>
          <button class="filter-btn" id="btn-pass" onclick="setFilter('pass')">僅合格</button>
        </div>
      </div>

      <!-- Revit 連動提示列 -->
      <div class="toast-bar">
        <div style="display:flex;align-items:center;gap:8px;">
          <span>🔗</span>
          <span id="revitToastText">請點選下方任一檢討空間列，系統將自動於 Revit 內縮放並選取該空間與最不利逃生路徑。</span>
        </div>
        <code class="code-tag" id="revitCmdTag">Ready (127.0.0.1:${port})</code>
      </div>

      <div class="card table-card">
        <div class="table-wrap">
          <table>
            <thead>
              <tr>
                <th>檢討空間 (點擊直接連動 Revit)</th>
                <th>對應最近出口位置</th>
                <th>各轉折線段標註尺寸 (≥ 10cm)</th>
                <th style="width:260px;">最遠路徑長度 / 法規上限</th>
                <th>檢討是否通過</th>
                <th style="text-align:right;">Revit 空間定位連結</th>
              </tr>
            </thead>
            <tbody id="resultTableBody"></tbody>
          </table>
        </div>
      </div>
    </div>
  </div>

  <script>
    const BridgePort = ${port};
    const RawData = ${safeJson};
    const Results = Array.isArray(RawData.Results) ? RawData.Results : [];
    const ValidRooms = Results.filter(r => r && r.Success);
    const MaxAllowed = Number(RawData.MaxAllowedMeters || 50);

    let currentFilter = 'all';
    let selectedRoomId = null;

    function initDashboard() {
      // 1. 檢討樓層
      const levelName = RawData.LevelName || RawData.ViewName || '當前檢核平面視圖';
      const elev = typeof RawData.LevelElevationMeters === 'number' ? RawData.LevelElevationMeters.toFixed(2) : '0.00';
      document.getElementById('cfg-level-name').textContent = levelName + (RawData.ViewName && RawData.ViewName !== levelName ? ' (' + RawData.ViewName + ')' : '');
      document.getElementById('cfg-level-sub').textContent = '樓層高程：' + (Number(elev) >= 0 ? '+' : '') + elev + ' m';
      document.getElementById('cfg-view-id').textContent = 'View ID: ' + (RawData.ViewId || 'Active');

      // 2. 檢討房間名稱
      document.getElementById('cfg-room-count').textContent = '共 ' + Results.length + ' 間居室';
      const roomChipsEl = document.getElementById('cfg-room-chips');
      roomChipsEl.innerHTML = '';
      Results.forEach(r => {
        const sp = document.createElement('span');
        const isFail = r.Success && r.IsCompliant !== '合格';
        sp.className = 'chip' + (isFail ? ' fail-chip' : '');
        sp.textContent = (r.RoomNumber ? '[' + r.RoomNumber + '] ' : '') + (r.RoomName || ('Room #' + r.RoomId));
        roomChipsEl.appendChild(sp);
      });

      // 3. 檢討出口位置
      const exits = Array.isArray(RawData.ExitDoors) ? RawData.ExitDoors : [];
      document.getElementById('cfg-exit-count').textContent = exits.length + ' 處出口';
      const exitListEl = document.getElementById('cfg-exit-list');
      exitListEl.innerHTML = '';
      exits.forEach(ex => {
        const div = document.createElement('div');
        div.className = 'exit-item';
        const assigned = Number(ex.AssignedRoomCount || 0);
        const assignedBadge = exits.length > 1
          ? '<span class="chip" style="margin-left:6px;">最近出口採用 ' + assigned + ' 間</span>'
          : '';
        div.innerHTML = '<span>🚪 ' + (ex.Label || ('Door #' + ex.DoorId)) + assignedBadge + '</span><span>#' + ex.DoorId + '</span>';
        exitListEl.appendChild(div);
      });
      if (exits.length > 1) {
        const note = document.createElement('div');
        note.className = 'cfg-sub';
        note.style.marginTop = '6px';
        note.textContent = '多出口模式：各空間自動擇定最近出口，每空間僅繪製 1 條動線';
        exitListEl.appendChild(note);
      }

      // 4. 法規適用類組
      document.getElementById('cfg-occupancy').textContent = RawData.OccupancyCategory || ('建築技術規則第 93 條（上限 ' + MaxAllowed + ' m）');
      document.getElementById('cfg-max-allowed').textContent = 'L ≤ ' + MaxAllowed.toFixed(2) + ' m';

      // KPI
      const total = Number(RawData.TotalRooms ?? Results.length);
      const passCount = Number(RawData.CompliantCount ?? ValidRooms.filter(r => r.IsCompliant === '合格').length);
      const failCount = Number(RawData.NonCompliantCount ?? ValidRooms.filter(r => r.IsCompliant !== '合格').length);
      const passRate = total > 0 ? Math.round((passCount / total) * 1000) / 10 : 0;

      document.getElementById('kpi-total').textContent = total + ' 間';
      document.getElementById('kpi-pass').textContent = passCount + ' 間';
      document.getElementById('kpi-pass-rate').textContent = passRate + '% 合格';
      document.getElementById('kpi-fail').textContent = failCount + ' 間';
      document.getElementById('kpi-fail-badge').textContent = failCount > 0 ? '需動線檢討' : '全數合格';

      const sortedByDist = [...ValidRooms].sort((a, b) => Number(b.ActualDistanceMeters || 0) - Number(a.ActualDistanceMeters || 0));
      if (sortedByDist.length > 0) {
        const worst = sortedByDist[0];
        const maxEl = document.getElementById('kpi-max-dist');
        maxEl.textContent = Number(worst.ActualDistanceMeters || 0).toFixed(2) + ' m';
        maxEl.style.color = worst.IsCompliant === '合格' ? '#10b981' : '#ef4444';
        document.getElementById('kpi-max-room').textContent = (worst.RoomNumber ? '[' + worst.RoomNumber + '] ' : '') + (worst.RoomName || '');
      }

      document.getElementById('btn-all').textContent = '全部 (' + Results.length + ')';
      document.getElementById('btn-fail').textContent = '僅不合格 (' + failCount + ')';
      document.getElementById('btn-pass').textContent = '僅合格 (' + passCount + ')';

      renderTable();
    }

    function renderTable() {
      const tbody = document.getElementById('resultTableBody');
      tbody.innerHTML = '';

      const sorted = [...Results].sort((a, b) => Number(b.ActualDistanceMeters || 0) - Number(a.ActualDistanceMeters || 0));
      const rows = sorted.filter(r => {
        if (currentFilter === 'fail') return r.Success && r.IsCompliant !== '合格';
        if (currentFilter === 'pass') return r.Success && r.IsCompliant === '合格';
        return true;
      });

      rows.forEach(room => {
        const tr = document.createElement('tr');
        const isSelected = room.RoomId === selectedRoomId;
        if (isSelected) tr.className = 'selected';

        if (!room.Success) {
          tr.innerHTML =
            '<td><strong>' + (room.RoomName || '未命名空間') + '</strong> <span style="color:var(--muted)">#' + room.RoomId + '</span></td>' +
            '<td colspan="3" style="color:#ef4444;">檢核失敗：' + (room.Error || '未知錯誤') + '</td>' +
            '<td><span class="status-pill status-fail">❌ 失敗</span></td>' +
            '<td style="text-align:right;"><button class="link-btn" onclick="event.stopPropagation();focusRoomInRevit(' + JSON.stringify(room).replace(/"/g, '&quot;') + ')">🔍 定位空間</button></td>';
          tr.onclick = () => focusRoomInRevit(room);
          tbody.appendChild(tr);
          return;
        }

        const isPass = room.IsCompliant === '合格';
        const actual = Number(room.ActualDistanceMeters || 0);
        const max = Number(room.MaxAllowedMeters || MaxAllowed);
        const pct = Math.min(100, Math.round((actual / max) * 100));
        const barColor = !isPass ? '#ef4444' : (pct >= 80 ? '#f59e0b' : '#10b981');

        const segs = Array.isArray(room.SegmentLengthsMeters) ? room.SegmentLengthsMeters : [];
        const segHtml = segs.length > 0
          ? segs.map(s => '<span class="chip">' + Number(s).toFixed(2) + 'm</span>').join(' <span style="color:var(--muted)">+</span> ') +
            ' <span style="color:var(--muted);margin-left:4px;">(' + segs.length + ' 段)</span>'
          : '<span style="color:var(--muted)">單一直線段</span>';

        const diffText = isPass
          ? '餘裕 ' + (max - actual).toFixed(2) + ' m (' + pct + '%)'
          : '超標 +' + (actual - max).toFixed(2) + ' m (' + Math.round((actual / max) * 100) + '%)';

        const candExits = Array.isArray(room.CandidateExits) ? room.CandidateExits : [];
        const autoBadge = room.NearestExitAutoSelected
          ? ' <span class="chip" style="color:#10b981;border-color:rgba(16,185,129,0.35);">自動擇近</span>'
          : '';
        const otherExitsHtml = candExits.length > 1
          ? '<div style="color:var(--muted);margin-top:2px;">比選：' +
              candExits.map(c =>
                (c.IsNearest ? '<strong style="color:var(--fg);">' : '<span>') +
                (c.Label || ('#' + c.DoorId)) + ' ' + Number(c.FarthestDistanceMeters || 0).toFixed(2) + 'm' +
                (c.IsNearest ? '</strong>' : '</span>')
              ).join(' ／ ') +
            '</div>'
          : '';

        tr.innerHTML =
          '<td>' +
            '<div style="display:flex;align-items:center;gap:10px;">' +
              '<span class="room-num-box ' + (isSelected ? 'active' : '') + '">' + (room.RoomNumber || '—') + '</span>' +
              '<div>' +
                '<div style="font-weight:700;font-size:13px;">' + (room.RoomName || '未命名房間') + ' <span style="font-weight:400;color:var(--muted);">#' + room.RoomId + '</span></div>' +
                (isSelected ? '<div style="color:#3b82f6;font-weight:600;margin-top:2px;">● 已連動 Revit 縮放並高亮顯示此空間與路徑</div>' : '') +
              '</div>' +
            '</div>' +
          '</td>' +
          '<td>' +
            '<div style="font-weight:600;">' + (room.TargetExit || '—') + autoBadge + '</div>' +
            '<div style="color:var(--muted);">Path ID: ' + (room.PathOfTravelId || '—') + '</div>' +
            otherExitsHtml +
          '</td>' +
          '<td><div style="display:flex;flex-wrap:wrap;align-items:center;gap:4px;">' + segHtml + '</div></td>' +
          '<td>' +
            '<div style="display:flex;justify-content:space-between;font-weight:700;margin-bottom:4px;">' +
              '<span style="color:' + (isPass ? 'var(--fg)' : '#ef4444') + ';font-size:13px;">' + actual.toFixed(2) + ' m</span>' +
              '<span style="color:' + (isPass ? 'var(--muted)' : '#ef4444') + ';">' + diffText + '</span>' +
            '</div>' +
            '<div class="progress-track"><div class="progress-bar" style="width:' + pct + '%;background:' + barColor + ';"></div></div>' +
          '</td>' +
          '<td>' +
            (isPass
              ? '<span class="status-pill status-pass">✅ 合格 PASS</span>'
              : '<span class="status-pill status-fail">🚨 不合格 FAIL</span>') +
          '</td>' +
          '<td style="text-align:right;">' +
            '<button class="link-btn ' + (isSelected ? 'active' : '') + '">' +
              (isSelected ? '📍 已在 Revit 顯示 ✓' : '🔍 點選於 Revit 顯示空間') +
            '</button>' +
          '</td>';

        tr.onclick = () => focusRoomInRevit(room);
        tbody.appendChild(tr);
      });
    }

    async function focusRoomInRevit(room) {
      selectedRoomId = room.RoomId;
      renderTable();

      const toastEl = document.getElementById('revitToastText');
      const cmdEl = document.getElementById('revitCmdTag');
      const label = (room.RoomNumber ? '[' + room.RoomNumber + '] ' : '') + (room.RoomName || ('Room #' + room.RoomId));

      toastEl.innerHTML = '⏳ 正在連動 Revit 視圖定位至 <strong>' + label + '</strong>...';
      cmdEl.textContent = 'zoom_to_element(' + (room.PathOfTravelId || room.RoomId) + ') + select_element([' + room.RoomId + ', ' + (room.PathOfTravelId || '') + '])';

      try {
        const res = await fetch('http://127.0.0.1:' + BridgePort + '/api/focus', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            roomId: room.RoomId,
            pathId: room.PathOfTravelId,
            segmentDimensionIds: room.SegmentDimensionIds || []
          })
        });
        const data = await res.json();
        if (data.ok) {
          toastEl.innerHTML = '✅ 已自動連結 Revit 視圖：目前選取並縮放至 <strong>' + label + ' (Room ID: ' + room.RoomId + ')</strong> 及最不利逃生路徑 <strong>(Path ID: ' + (room.PathOfTravelId || '—') + ')</strong>';
        } else {
          toastEl.innerHTML = '⚠️ Revit 連動回應異常：' + (data.error || '請確認 Revit 視窗未處於編輯模式');
        }
      } catch (err) {
        toastEl.innerHTML = '⚠️ 無法連線至本機橋接服務 (127.0.0.1:' + BridgePort + ')，請確認 MCP Server 仍在執行中。';
      }
    }

    function setFilter(mode) {
      currentFilter = mode;
      ['all', 'fail', 'pass'].forEach(k => {
        document.getElementById('btn-' + k).className = 'filter-btn' + (k === mode ? ' active' : '');
      });
      renderTable();
    }

    async function triggerSheetExport() {
      const sheetNumber = prompt('請輸入要放置「法規檢討說明文字」的目標圖號（明細表將僅建立於 Revit 專案明細表清單，不直接排版至圖紙）：', 'A-101');
      if (!sheetNumber) return;

      const btn = document.getElementById('sheetBtn');
      btn.textContent = '⏳ 正在輸出法規說明至圖紙 ' + sheetNumber + '...';

      const sortedByDist = [...ValidRooms].sort((a, b) => Number(b.ActualDistanceMeters || 0) - Number(a.ActualDistanceMeters || 0));
      const worst = sortedByDist[0] || {};
      const passCount = Number(RawData.CompliantCount ?? 0);
      const failCount = Number(RawData.NonCompliantCount ?? 0);
      const totalRooms = Number(RawData.TotalRooms ?? Results.length);
      const conclusion = failCount === 0
        ? '本層各居室步行距離皆符合建築技術規則第93條之規定。'
        : '部分居室超出法定上限，需增設安全梯、調整走廊動線或檢討自動撒水設備放寬規定。';

      const exitsForNote = Array.isArray(RawData.ExitDoors) ? RawData.ExitDoors : [];
      const exitListText = exitsForNote.map(ex => ex.Label || ('#' + ex.DoorId)).join('、');
      const methodText = exitsForNote.length > 1
        ? '3. 檢討方式：以候選出口（' + exitListText + '）分別實測各居室最遠角落（內縮30cm基準點）經由實際可通行路徑（Path of Travel）之步行距離，並自動擇定距離最短之最近出口作為該居室檢討值。'
        : '3. 檢討方式：由各居室最遠角落（內縮30cm基準點）經由實際可通行路徑（Path of Travel）至最近安全梯防火門中心點進行實測。';

      const noteText = [
        '【建築技術規則第93條：避難層以外樓層之步行距離檢討說明】',
        '1. 檢討依據：依《建築技術規則》建築設計施工編第93條規定辦理。',
        '2. 空間條件：' + (RawData.OccupancyCategory || '本樓層主要構造為防火構造') + '，檢討居室至直通樓梯（安全梯）之最大容許步行距離為 ' + MaxAllowed + ' 公尺。',
        methodText,
        '4. 檢討結果綜整：',
        '   - 檢核居室總數：共 ' + totalRooms + ' 間',
        '   - 實測最大步行距離：' + (worst.RoomNumber || '') + ' ' + (worst.RoomName || '') + '（至 ' + (worst.TargetExit || '') + '），實測距離 L = ' + Number(worst.ActualDistanceMeters || 0).toFixed(2) + ' m（法規上限 ' + MaxAllowed + ' m）',
        '   - 合格數：' + passCount + ' 間 ／ 不合格數：' + failCount + ' 間',
        '5. 結論：' + conclusion
      ].join('\\n');

      try {
        const res = await fetch('http://127.0.0.1:' + BridgePort + '/api/export-sheet', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ sheetNumber, regulationNoteText: noteText, placeScheduleOnSheet: false })
        });
        const out = await res.json();
        btn.textContent = out.ok ? ('✓ 已輸出法規說明至圖紙 ' + sheetNumber + '（明細表存於專案）') : ('❌ 輸出失敗：' + out.error);
      } catch (e) {
        btn.textContent = '❌ 輸出失敗';
      }
      setTimeout(() => { btn.textContent = '📝 輸出法規說明至圖紙（明細表僅存於專案）'; }, 3500);
    }

    initDashboard();
  </script>
</body>
</html>`;
}

/**
 * 啟動或更新外部視覺化報告並於預設瀏覽器開啟
 */
export async function launchEgressVisualReport(
    data: any,
    client: RevitSocketClient,
    openBrowser: boolean = true
): Promise<{ reportUrl: string; reportFilePath: string }> {
    const port = await ensureEgressReportBridge(client);
    const html = buildEgressVisualReportHtml(data, port);
    latestReportHtml = html;

    const reportFilePath = path.join(os.tmpdir(), "RevitMCP_EgressReport.html");
    try {
        fs.writeFileSync(reportFilePath, html, "utf-8");
    } catch {
        // 忽略暫存檔寫入例外
    }

    const reportUrl = `http://127.0.0.1:${port}/report`;
    if (openBrowser) {
        openUrlInDefaultBrowser(reportUrl);
    }

    return { reportUrl, reportFilePath };
}

/**
 * 將 C# 端回傳的房間避難檢核結果格式化為易讀報告與結構化資料
 */
export function formatEgressAuditResponse(
    data: any,
    reportInfo?: { reportUrl: string; reportFilePath: string }
): string {
    if (!data || typeof data !== "object") {
        return JSON.stringify(data, null, 2);
    }

    const totalRooms = data.TotalRooms ?? 0;
    const compliantCount = data.CompliantCount ?? 0;
    const nonCompliantCount = data.NonCompliantCount ?? 0;
    const failedCount = data.FailedCount ?? 0;
    const maxAllowedMeters = data.MaxAllowedMeters ?? 0;
    const levelName = data.LevelName || data.ViewName || "未指定";
    const occupancyCategory = data.OccupancyCategory || `第 93 條（上限 ${maxAllowedMeters} m）`;
    const results: any[] = Array.isArray(data.Results) ? data.Results : [];

    const lines: string[] = [
        `=== 房間避難步行距離檢核報告 ===`,
        `檢討樓層：${levelName}`,
        `法規適用類組：${occupancyCategory}（法規容許上限：${maxAllowedMeters} m）`,
        `檢核房間總數：${totalRooms} 間（合格：${compliantCount}｜不合格：${nonCompliantCount}｜失敗：${failedCount}）`,
        `Revit 專案明細表：${data.ScheduleName || "避難步行距離檢討表"}${data.ScheduleId ? ` (Schedule ID: ${data.ScheduleId})` : ""}（已建立於 Revit 明細表清單，未直接排版至圖紙上）`,
    ];

    if (reportInfo) {
        lines.push(
            `外部視覺化總結報告（支援點選空間直接連動 Revit）：${reportInfo.reportUrl}`,
            `本機報告檔案路徑：${reportInfo.reportFilePath}`
        );
    }

    lines.push(``, `--- 各房間檢核明細 ---`);

    for (const item of results) {
        const roomLabel = `${item.RoomNumber ? `[${item.RoomNumber}] ` : ""}${item.RoomName || "未命名房間"} (ID: ${item.RoomId})`;
        if (!item.Success) {
            lines.push(`❌ ${roomLabel}：檢核失敗 — ${item.Error || "未知錯誤"}`);
            continue;
        }

        const badge = item.IsCompliant === "合格" ? "✅ [合格]" : "🚨 [不合格]";
        const segLengths: number[] = Array.isArray(item.SegmentLengthsMeters) ? item.SegmentLengthsMeters : [];
        const segDimIds: number[] = Array.isArray(item.SegmentDimensionIds) ? item.SegmentDimensionIds : [];
        const segSummary =
            segLengths.length > 0
                ? `｜各轉折線段：${segLengths.map((len) => `${len} m`).join(" + ")}（已標註 ${segDimIds.length} 段）`
                : "";
        const candExits: any[] = Array.isArray(item.CandidateExits) ? item.CandidateExits : [];
        const candSummary =
            candExits.length > 1
                ? `｜多出口比選：${candExits
                      .map((c) => `${c.IsNearest ? "★" : ""}${c.Label} ${c.FarthestDistanceMeters} m`)
                      .join(" / ")}`
                : "";
        lines.push(
            `${badge} ${roomLabel}：實測最遠步行距離 ${item.ActualDistanceMeters} m / 上限 ${item.MaxAllowedMeters} m｜最近逃生出口：${item.TargetExit}｜路徑 ID: ${item.PathOfTravelId}${item.TagId ? `｜標籤 ID: ${item.TagId}` : ""}${segSummary}${candSummary}`
        );
    }

    lines.push(``, `--- 原始 JSON 資料 ---`, JSON.stringify(data, null, 2));
    return lines.join("\n");
}

/**
 * 將 C# 端回傳的圖紙自動排版結果格式化回傳給 AI
 */
export function formatGenerateEgressSheetNoteResponse(sheetNumber: string, data: any): string {
    const resolvedSheetNumber = data?.SheetNumber || sheetNumber;
    const placedSchedule = data?.PlacedScheduleOnSheet === true;
    const header = placedSchedule
        ? `已成功於圖紙 ${resolvedSheetNumber} 建立法規檢討明細表與備註`
        : `已於 Revit 專案建立「${data?.ScheduleName || "避難步行距離檢討表"}」明細表（不直接排版至圖紙），並於圖紙 ${resolvedSheetNumber} 放置法規檢討說明文字`;

    if (!data || typeof data !== "object") {
        return header;
    }

    return `${header}\n\n--- 詳細資訊 ---\n${JSON.stringify(data, null, 2)}`;
}
