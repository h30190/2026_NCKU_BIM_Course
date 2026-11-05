/**
 * 排煙窗法規檢討工具 — mep, fire-safety Profile
 * 來源：PR#12 (@7alexhuang-ux)，經跨版本修正後整合
 * 法規：建技規§101① + 消防§188
 */

import { Tool } from "@modelcontextprotocol/sdk/types.js";

export const smokeExhaustTools: Tool[] = [
    {
        name: "plan_smoke_exhaust_window_plan",
        description: "Read only: finds up to the requested number of legal candidate placements on the selected Room's largest outer boundary loop, prioritizing exterior Walls and avoiding existing Windows and boundary ends. Stores a plan only when every requested placement is found.",
        inputSchema: {
            type: "object",
            properties: {
                roomId: { type: "number", description: "Selected Revit Room ElementId" },
                windowType: { type: "string", description: "Exact Window type name, preferably FamilyName: TypeName" },
                count: { type: "integer", minimum: 1, description: "Requested number of new Windows; at least 1" },
                sillHeightMm: { type: "number", description: "Window sill height above the Room level, in millimeters" },
                widthMm: { type: "number", description: "Candidate Width in mm; must match the Revit type if provided" },
                heightMm: { type: "number", description: "Candidate Height in mm; must match the Revit type if provided" },
            },
            required: ["roomId", "windowType", "count", "sillHeightMm"],
        },
    },
    {
        name: "apply_smoke_exhaust_window_plan",
        description: "Creates all Windows from a stored smoke exhaust plan in one Revit transaction. A failed placement rolls back the complete plan.",
        inputSchema: {
            type: "object",
            properties: {
                planId: { type: "string", description: "PlanId returned by plan_smoke_exhaust_window_plan" },
                roomId: { type: "number", description: "Required only when supplying placements directly" },
                windowType: { type: "string", description: "Required only when supplying placements directly" },
                placements: {
                    type: "array",
                    description: "Optional complete placements; supply at least one when planId is omitted",
                    items: {
                        type: "object",
                        properties: {
                            hostWallId: { type: "number" }, locationX: { type: "number" }, locationY: { type: "number" },
                            sillHeightMm: { type: "number" }, widthMm: { type: "number" }, heightMm: { type: "number" },
                        },
                        required: ["hostWallId", "locationX", "locationY", "sillHeightMm", "widthMm", "heightMm"],
                    },
                },
            },
        },
    },
    {
        name: "undo_smoke_exhaust_window_plan",
        description: "Reverses one successful smoke exhaust Window plan by deleting only its recorded Window ElementIds in one Revit transaction.",
        inputSchema: {
            type: "object",
            properties: { operationId: { type: "string", description: "OperationId returned by apply_smoke_exhaust_window_plan" } },
            required: ["operationId"],
        },
    },
    {
        name: "plan_smoke_exhaust_window_retrofit",
        description: "Read only: simulates creating or reusing a larger Type within the two existing Windows' current Family, then changing their Type and Sill Height without moving or rehosting them. Returns a ProposedTypeSpec, original/proposed snapshots, checker-rounded opening areas, ceiling validation, and an applyable PASS plan.",
        inputSchema: {
            type: "object",
            properties: {
                roomId: { type: "number", description: "Selected Revit Room ElementId" },
                targetWindowIds: { type: "array", items: { type: "number" }, minItems: 2, maxItems: 2, description: "Exactly two existing Window ElementIds belonging to the Room" },
                targetWindowType: { type: "string", description: "Proposed same-Family Window type name, preferably FamilyName: TypeName; the type may not yet exist" },
                targetWidthMm: { type: "number", description: "Proposed same-Family Type Width in millimeters" },
                targetHeightMm: { type: "number", description: "Proposed same-Family Type Height in millimeters" },
                targetSillHeightMm: { type: "number", description: "Proposed Window sill height above the Room level in millimeters" },
            },
            required: ["roomId", "targetWindowIds", "targetWindowType", "targetWidthMm", "targetHeightMm", "targetSillHeightMm"],
        },
    },
    {
        name: "apply_smoke_exhaust_window_retrofit",
        description: "Applies a validated PASS plan in one Revit transaction, duplicating the source Window FamilySymbol when needed, setting only the duplicate's Width/Height, and changing exactly two existing Windows. Preserves each ElementId, Host Wall, XY, Window Inset value/global associations, and facing/hand; any failure rolls back all model changes.",
        inputSchema: {
            type: "object",
            properties: { planId: { type: "string", description: "PlanId returned by plan_smoke_exhaust_window_retrofit" } },
            required: ["planId"],
        },
    },
    {
        name: "undo_smoke_exhaust_window_retrofit",
        description: "Restores the original Type and Sill Height of two retrofitted Windows in one Revit transaction without deleting or rehosting them. Deletes the operation-created Type only when no FamilyInstances use it; otherwise retains it. Rechecks the original Room result.",
        inputSchema: {
            type: "object",
            properties: { operationId: { type: "string", description: "OperationId returned by apply_smoke_exhaust_window_retrofit" } },
            required: ["operationId"],
        },
    },
    {
        name: "plan_smoke_exhaust_window_replacement",
        description: "Read only: selects fixed Room Windows by host geometry and spacing, snapshots instance/global-parameter state, checks target-family parameter compatibility, and predicts checker effective area. Stores a short-lived plan only.",
        inputSchema: {
            type: "object",
            properties: {
                roomId: { type: "number" },
                targetWindowType: { type: "string", description: "Loaded Window Family: Type name" },
                targetSillHeightMm: { type: "number" },
                requiredCount: { type: "integer", minimum: 1 },
            },
            required: ["roomId", "targetWindowType", "targetSillHeightMm", "requiredCount"],
        },
    },
    {
        name: "apply_smoke_exhaust_window_replacement",
        description: "Atomically replaces the planned fixed Windows without cross-Family ChangeTypeId. Only a ReplacementSafe PASS plan can be applied; checker revalidation is part of the transaction.",
        inputSchema: { type: "object", properties: { planId: { type: "string" } }, required: ["planId"] },
    },
    {
        name: "undo_smoke_exhaust_window_replacement",
        description: "Atomically removes replacement Windows and recreates the original Family Types, hosts, locations, parameters, orientations, and global-parameter associations from the operation snapshot.",
        inputSchema: { type: "object", properties: { operationId: { type: "string" } }, required: ["operationId"] },
    },
    {
        name: "check_smoke_exhaust_windows",
        description: "排煙窗檢討：檢查天花板下 80cm 內可開啟窗面積是否 ≥ 區劃面積 2%。同時判定無窗居室。法源：建技規§101① + 消防§188。自動上色：綠=全開、黃=折減、紅=固定。",
        inputSchema: {
            type: "object",
            properties: {
                levelName: { type: "string", description: "樓層名稱" },
                ceilingHeightSource: { type: "string", enum: ["room_parameter", "ceiling_element"], description: "天花板高度來源", default: "room_parameter" },
                colorize: { type: "boolean", description: "是否自動上色窗戶", default: true },
                smokeZoneHeight: { type: "number", description: "有效帶高度（mm），預設 800", default: 800 },
                excludeKeywords: { type: "array", items: { type: "string" }, description: "非居室排除關鍵字" },
            },
            required: ["levelName"],
        },
    },
    {
        name: "check_floor_effective_openings",
        description: "無開口樓層判定：檢查樓層外牆有效開口面積是否 ≥ 樓地板面積 1/30。法源：消防§4 + §28③。",
        inputSchema: {
            type: "object",
            properties: {
                levelName: { type: "string", description: "樓層名稱" },
                colorize: { type: "boolean", description: "是否自動上色開口", default: true },
            },
            required: ["levelName"],
        },
    },
    {
        name: "create_section_view",
        description: "建立剖面視圖，面向指定牆面。可用於檢視窗戶與天花板的高度關係。",
        inputSchema: {
            type: "object",
            properties: {
                wallId: { type: "number", description: "目標牆的 Element ID" },
                viewName: { type: "string", description: "視圖名稱", default: "排煙檢討剖面" },
                offset: { type: "number", description: "剖面距牆偏移（mm），預設 1000", default: 1000 },
                scale: { type: "number", description: "比例尺（如 50 = 1:50），預設 50", default: 50 },
            },
            required: ["wallId"],
        },
    },
    {
        name: "create_detail_lines",
        description: "在視圖上繪製詳圖線（天花板線、有效帶範圍線等），可指定顏色和標籤。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "目標視圖的 Element ID" },
                lines: {
                    type: "array",
                    description: "線段陣列",
                    items: {
                        type: "object",
                        properties: {
                            startX: { type: "number", description: "起點 X（mm）" },
                            startY: { type: "number", description: "起點 Y（mm）" },
                            endX: { type: "number", description: "終點 X（mm）" },
                            endY: { type: "number", description: "終點 Y（mm）" },
                            color: { type: "object", properties: { r: { type: "number" }, g: { type: "number" }, b: { type: "number" } } },
                            lineStyle: { type: "string", description: "線條樣式（選填）" },
                            label: { type: "string", description: "標籤（選填）" },
                        },
                        required: ["startX", "startY", "endX", "endY"],
                    },
                },
            },
            required: ["viewId", "lines"],
        },
    },
    {
        name: "create_filled_region",
        description: "建立填充區域（如排煙有效帶色塊），可設定顏色和透明度。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "目標視圖的 Element ID" },
                points: {
                    type: "array",
                    description: "多邊形頂點（至少 3 個點，自動封閉）",
                    items: { type: "object", properties: { x: { type: "number" }, y: { type: "number" } }, required: ["x", "y"] },
                },
                color: { type: "object", properties: { r: { type: "number" }, g: { type: "number" }, b: { type: "number" } } },
                transparency: { type: "number", description: "透明度 0-100，預設 50", default: 50 },
                regionType: { type: "string", description: "填充區域類型名稱（選填）" },
            },
            required: ["viewId", "points"],
        },
    },
    {
        name: "create_text_note",
        description: "在視圖上建立文字標註。",
        inputSchema: {
            type: "object",
            properties: {
                viewId: { type: "number", description: "目標視圖的 Element ID" },
                x: { type: "number", description: "X 座標（mm）" },
                y: { type: "number", description: "Y 座標（mm）" },
                text: { type: "string", description: "文字內容" },
                textSize: { type: "number", description: "文字大小（mm），預設 2.5", default: 2.5 },
            },
            required: ["viewId", "x", "y", "text"],
        },
    },
    {
        name: "export_smoke_review_excel",
        description: "匯出排煙窗檢討 Excel 報告（.xlsx），含樓層總覽、房間明細、窗戶明細、改善建議、§101補充檢討（排風量提醒+中央管理室偵測）五個工作表。",
        inputSchema: {
            type: "object",
            properties: {
                levelName: { type: "string", description: "樓層名稱" },
                ceilingHeightSource: { type: "string", enum: ["room_parameter", "ceiling_element"], default: "room_parameter" },
                outputPath: { type: "string", description: "輸出路徑（選填）" },
            },
            required: ["levelName"],
        },
    },
];
