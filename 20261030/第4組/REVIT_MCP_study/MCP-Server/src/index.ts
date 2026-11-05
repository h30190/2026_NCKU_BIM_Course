#!/usr/bin/env node

/**
 * Revit MCP Server
 * Bridge between AI and Revit via MCP
 */

import { Server } from "@modelcontextprotocol/sdk/server/index.js";
import { StdioServerTransport } from "@modelcontextprotocol/sdk/server/stdio.js";
import http, { IncomingMessage, ServerResponse } from "node:http";
import {
  CallToolRequestSchema,
  ListToolsRequestSchema,
  ListResourcesRequestSchema,
  ReadResourceRequestSchema,
  Tool,
} from "@modelcontextprotocol/sdk/types.js";
import { RevitSocketClient } from "./socket.js";
import { registerRevitTools, executeRevitTool } from "./tools/revit-tools.js";
import { listAppResources, readAppResource } from "./apps/register-apps.js";

// MCP Server Instance
const server = new Server(
  {
    name: "revit-mcp-server",
    version: "1.0.0",
  },
  {
    capabilities: {
      tools: {},
      // MCP Apps (io.modelcontextprotocol/ui): serve ui:// HTML resources for interactive tools.
      resources: {},
    },
  }
);

// Revit Socket Client
const revitClient = new RevitSocketClient();

const BROWSER_TOOLS = new Set(["get_all_levels", "get_rooms_by_level", "check_smoke_exhaust_windows", "get_room_info", "plan_smoke_exhaust_window_plan", "apply_smoke_exhaust_window_plan", "undo_smoke_exhaust_window_plan", "plan_smoke_exhaust_window_retrofit", "apply_smoke_exhaust_window_retrofit", "undo_smoke_exhaust_window_retrofit", "plan_smoke_exhaust_window_replacement", "apply_smoke_exhaust_window_replacement", "undo_smoke_exhaust_window_replacement", "select_element", "zoom_to_element"]);
const MAX_BROWSER_BODY_BYTES = 16 * 1024;
const DASHBOARD_HTML_URI = "ui://smoke-exhaust-dashboard/index.html";

function sendJson(res: ServerResponse, status: number, body: unknown): void {
  res.writeHead(status, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
  res.end(JSON.stringify(body));
}

function isAllowedLocalHost(hostHeader: string | undefined, port: number): boolean {
  if (!hostHeader) return false;
  const separator = hostHeader.lastIndexOf(":");
  const hostname = separator >= 0 ? hostHeader.slice(0, separator).replace(/^\[|\]$/g, "") : hostHeader;
  const hostPort = separator >= 0 ? hostHeader.slice(separator + 1) : "80";
  return (hostname === "127.0.0.1" || hostname === "localhost") && hostPort === String(port);
}

async function readJsonBody(req: IncomingMessage, maxBytes = MAX_BROWSER_BODY_BYTES): Promise<Record<string, unknown>> {
  const chunks: Buffer[] = [];
  let size = 0;
  for await (const chunk of req) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    size += buffer.length;
    if (size > maxBytes) throw Object.assign(new Error("Request body too large"), { statusCode: 413 });
    chunks.push(buffer);
  }
  let value: unknown;
  try { value = JSON.parse(Buffer.concat(chunks).toString("utf8")); }
  catch { throw Object.assign(new Error("Invalid JSON body"), { statusCode: 400 }); }
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    throw Object.assign(new Error("JSON body must be an object"), { statusCode: 400 });
  }
  return value as Record<string, unknown>;
}

async function callSharedTool(name: string, args: Record<string, any>, browser = false): Promise<any> {
  if (!revitClient.isConnected()) {
    if (browser) revitClient.clientName = "smoke-exhaust-dashboard";
    else {
      const clientInfo = server.getClientVersion();
      revitClient.clientName = clientInfo?.name || "unknown";
    }
    await revitClient.connect();
  }
  return executeRevitTool(name, args, revitClient);
}

function createDashboardHttpServer(port: number) {
  return http.createServer(async (req, res) => {
    const host = req.headers.host;
    if (!isAllowedLocalHost(host, port)) { sendJson(res, 403, { error: "Localhost requests only" }); return; }
    const url = new URL(req.url || "/", `http://${host}`);

    if (req.method === "GET" && (url.pathname === "/" || url.pathname === "/smoke-exhaust")) {
      const html = readAppResource(DASHBOARD_HTML_URI)?.contents?.[0];
      if (!html || !("text" in html)) { sendJson(res, 500, { error: "Dashboard HTML unavailable; build the MCP Server first." }); return; }
      res.writeHead(200, { "Content-Type": "text/html; charset=utf-8", "Cache-Control": "no-store" });
      res.end(html.text);
      return;
    }

    if (url.pathname === "/api/tools/call") {
      if (req.method !== "POST") { res.setHeader("Allow", "POST"); sendJson(res, 405, { error: "POST required" }); return; }
      if (!host || req.headers.origin !== `http://${host}`) { sendJson(res, 403, { error: "Same-origin requests only" }); return; }
      if (!req.headers["content-type"]?.toLowerCase().startsWith("application/json")) {
        sendJson(res, 415, { error: "Content-Type must be application/json" }); return;
      }
      try {
        const body = await readJsonBody(req);
        const name = body.name;
        if (typeof name !== "string" || !BROWSER_TOOLS.has(name)) { sendJson(res, 403, { error: "Tool is not allowed" }); return; }
        const rawArgs = body.arguments;
        if (rawArgs != null && (typeof rawArgs !== "object" || Array.isArray(rawArgs))) {
          sendJson(res, 400, { error: "arguments must be an object" }); return;
        }
        const args = { ...(rawArgs as Record<string, any> | undefined) };
        if (name === "check_smoke_exhaust_windows") args.colorize = false;
        const result = await callSharedTool(name, args, true);
        sendJson(res, 200, { content: [{ type: "text", text: JSON.stringify(result) }] });
      } catch (error) {
        const status = typeof (error as { statusCode?: unknown })?.statusCode === "number"
          ? (error as { statusCode: number }).statusCode : 500;
        sendJson(res, status, { error: error instanceof Error ? error.message : String(error) });
      }
      return;
    }
    sendJson(res, 404, { error: "Not found" });
  });
}

/**
 * Handle List Tools Request
 */
server.setRequestHandler(ListToolsRequestSchema, async () => {
  const tools = registerRevitTools();
  console.error(`[MCP Server] Registered ${tools.length} Revit tools`);
  return { tools };
});

/**
 * Handle List Resources Request (MCP Apps: ui:// interactive UI resources)
 */
server.setRequestHandler(ListResourcesRequestSchema, async () => {
  return { resources: listAppResources() };
});

/**
 * Handle Read Resource Request (serves the ui:// App HTML)
 */
server.setRequestHandler(ReadResourceRequestSchema, async (request) => {
  const resource = readAppResource(request.params.uri);
  if (!resource) {
    throw new Error(`Resource not found: ${request.params.uri}`);
  }
  return resource;
});

/**
 * Handle Call Tool Request
 */
server.setRequestHandler(CallToolRequestSchema, async (request) => {
  console.error(`[MCP Server] Calling tool: ${request.params.name}`);
  console.error(`[MCP Server] Arguments:`, JSON.stringify(request.params.arguments, null, 2));

  try {
    const result = await callSharedTool(request.params.name, request.params.arguments || {});

    console.error(`[MCP Server] Tool executed successfully`);

    return {
      content: [
        {
          type: "text",
          text: typeof result === "string" ? result : JSON.stringify(result, null, 2),
        },
      ],
    };
  } catch (error) {
    const errorMessage = error instanceof Error ? error.message : String(error);
    console.error(`[MCP Server] Tool execution failed: ${errorMessage}`);

    return {
      content: [
        {
          type: "text",
          text: `Error: ${errorMessage}`,
        },
      ],
      isError: true,
    };
  }
});

/**
 * Start Server
 */
async function main() {
  console.error("Revit MCP Server starting...");
  console.error("Waiting for Revit Plugin...");

  const transport = new StdioServerTransport();
  await server.connect(transport);

  const configuredPort = process.env.REVIT_MCP_PORT || "8964";
  console.error("MCP Server Started");
  console.error(`Socket Server listening on ${configuredPort}`);

  const dashboardPort = Number.parseInt(process.env.SMOKE_DASHBOARD_PORT || "10002", 10);
  if (!Number.isInteger(dashboardPort) || dashboardPort < 1024 || dashboardPort > 65535 || dashboardPort === Number(configuredPort)) {
    throw new Error(`Invalid SMOKE_DASHBOARD_PORT: ${process.env.SMOKE_DASHBOARD_PORT || dashboardPort}`);
  }
  const dashboardServer = createDashboardHttpServer(dashboardPort);
  await new Promise<void>((resolve, reject) => {
    dashboardServer.once("error", reject);
    dashboardServer.listen(dashboardPort, "127.0.0.1", () => {
      dashboardServer.removeListener("error", reject);
      resolve();
    });
  });
  console.error("Smoke Exhaust Dashboard:");
  console.error(`http://127.0.0.1:${dashboardPort}/`);

  // 處理優雅退出
  const shutdown = async () => {
    console.error("\n[MCP Server] 正在關閉伺服器...");
    try {
      await new Promise<void>((resolve) => dashboardServer.close(() => resolve()));
      await revitClient.disconnect();
      console.error("[MCP Server] 已中斷 Revit 連線");
    } catch (e) {
      // 忽略關閉時的錯誤
    }
    process.exit(0);
  };

  process.on("SIGINT", shutdown);
  process.on("SIGTERM", shutdown);
}

main().catch((error) => {
  console.error("Server startup failed", error);
  process.exit(1);
});
