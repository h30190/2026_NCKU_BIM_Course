/**
 * Revit MCP 工具定義 — 相容層
 *
 * 此檔案保留為相容層，實際工具定義已拆分到模組檔案：
 *   base-tools.ts, wall-tools.ts, room-tools.ts,
 *   visualization-tools.ts, schedule-tools.ts, mep-tools.ts
 *
 * 匯總與 Profile 篩選邏輯在 index.ts
 */

import { RevitSocketClient } from "../socket.js";
import { Tool } from "@modelcontextprotocol/sdk/types.js";
import { registerRevitTools as registerProfileTools } from "./index.js";
import {
    auditRoomEgressInputSchema,
    formatEgressAuditResponse,
    generateEgressSheetNoteInputSchema,
    formatGenerateEgressSheetNoteResponse,
    launchEgressVisualReport,
} from "./egress-audit-tools.js";

/**
 * 依 Profile 註冊工具（實際 Profile 篩選邏輯在 index.ts）
 */
export function registerRevitTools(): Tool[] {
    return registerProfileTools();
}

/**
 * 執行 Revit 工具
 */
export async function executeRevitTool(
    toolName: string,
    args: Record<string, any>,
    client: RevitSocketClient
): Promise<any> {
    // 將工具名稱轉換為 Revit 命令名稱
    // 如果是 query_elements_with_filter，映射到 C# 的 query_elements
    const commandName = toolName === "query_elements_with_filter" ? "query_elements" : toolName;

    // 若為避難檢核或圖紙排版指令，先使用 Zod Schema 驗證輸入參數
    let validatedArgs: Record<string, any> = args;
    if (commandName === "audit_room_egress") {
        validatedArgs = auditRoomEgressInputSchema.parse(args);
    } else if (commandName === "generate_egress_sheet_note") {
        validatedArgs = generateEgressSheetNoteInputSchema.parse(args);
    }

    // 跨文件命令需要較長 timeout（開啟 .rvt/.rfa 檔案較費時）
    const CROSS_DOC_COMMANDS = new Set(["read_source_file_sheets", "copy_sheets_from_file", "sync_sheet_parameters_from_source", "inject_green_material_into_family"]);
    // 避難路徑檢核在多出口時需計算「房間角落數 × 出口數」條 PathOfTravel，給予較長 timeout
    const LONG_RUNNING_COMMANDS = new Set(["audit_room_egress"]);
    const timeoutMs = CROSS_DOC_COMMANDS.has(commandName)
        ? 120000
        : LONG_RUNNING_COMMANDS.has(commandName)
            ? 180000
            : 30000;

    // 發送命令到 Revit
    const response = await client.sendCommand(commandName, validatedArgs, timeoutMs);

    if (!response.success) {
        throw new Error(response.error || "命令執行失敗");
    }

    if (commandName === "audit_room_egress") {
        let reportInfo: { reportUrl: string; reportFilePath: string } | undefined;
        try {
            reportInfo = await launchEgressVisualReport(
                response.data,
                client,
                validatedArgs.openReport !== false
            );
        } catch {
            // 若啟動外部報告橋接失敗，不阻斷檢核資料回傳
        }
        return formatEgressAuditResponse(response.data, reportInfo);
    }

    if (commandName === "generate_egress_sheet_note") {
        return formatGenerateEgressSheetNoteResponse(validatedArgs.sheetNumber, response.data);
    }

    return response.data;
}
