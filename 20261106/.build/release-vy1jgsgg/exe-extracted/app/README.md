# Case-driven BIM Model Diagnosis and Repair System

[繁體中文](README.zh-TW.md)

Project: **案例驅動之 BIM 模型診斷與修正系統開發**

Revit ribbon: **案例驅動 BIM 診斷與修正**

Release: **2026.11.04.2 · Windows x64 · Revit 2022**

## Install

1. Install Autodesk Revit 2022 and a compatible **pyRevit with its CLI**, attached to Revit 2022. Confirm the pyRevit tab loads.
2. Save your model and close Revit.
3. Double-click **CaseDrivenBIM-2026.11.04.2-Revit2022-Setup.exe**, then click **Install**.
4. The installer verifies the package, installs private Python/dependencies, checks an isolated backend and registers the extension.
5. Open Revit 2022. Use **案例驅動 BIM 診斷與修正 → 輔助工具 → 開啟面板**.

Installation works offline, without administrator rights or a separate Python installation. Revit and pyRevit are prerequisites, not bundled. This package contains the **Revit 2022** shaft engine; other versions need a matching build and validation. The installer is unsigned; Windows may show a publisher warning. A SHA-256 checksum accompanies the release.

Default location: `%LOCALAPPDATA%\Programs\CaseDrivenBIM\app`. The Start menu's **Case-driven BIM** folder contains both guides and **Check Installation**. Remove an older development copy's extension search path in pyRevit settings before using the installed copy, to avoid loading duplicates.

## Main functions

| Ribbon panel | Functions |
| --- | --- |
| 模型檢查 | Duplicate elements, overlaps, clashes, marks and Revit warnings |
| 法規檢查 | Shaft fire separation, maintenance/access and pipe separation |
| 問題與解法 | Issue records, solution review, case-based/AI suggestions and repair |
| 輔助工具 | Similar elements, comparison, relationship graph and case library |

Gray regulation buttons are placeholders; only **管道間檢查** is implemented in that panel.

### Model checks and issues

Open **建模檢查**, select scope/options and run the check. For a two-element result, **定位** selects the **second/right-hand element**. Transfer a finding to **問題紀錄**, review it, then save a draft or submit. Review and verify solutions in **解法審查** before reusing them as verified references.

### Shaft checks and CSV

Open **管道間檢查 → 檢查主模型及已載入連結**. Select a finding to locate elements, create an issue or open modification suggestions. **匯出問題 CSV** exports all violations, warnings and missing-data findings to your chosen location. Each problem gets its own row, including multiple problems in one shaft.

The CSV contains source/model, room/level, rule, measurements, advice and element IDs. UTF-8 BOM preserves Chinese text in Excel. Unresolved sources and coverage limitations are included; passes are excluded. Run checks again after model/document changes. Linked element IDs retain their source; navigation selects the host link instance.

Rules: `ShaftChecker-main/src/ShaftChecker.Revit/config/shaft-rules.json`. Evaluate these rules against your project's requirements.

### Repairs

Pin the target and select related Revit elements. In **修改建議**, choose **透過過去案例** or **透過 AI 建議** and generate a plan. Each suggestion has its own **套用** button; **套用全部** skips completed items. **自動修正** generates and applies a plan.

Changes are restricted to the target/current selection. A repaired pair does not mark another pair sharing its element as repaired. Failed/uncertain operations stop subsequent changes; inspect the model before planning again.

## Optional Claude / Revit MCP

Model checks, issue records and CSV work without AI. Planning/MCP repairs require your own Claude CLI login, Node.js and compatible Revit MCP add-in/server. These programs and credentials are not bundled.

1. Install Claude CLI and sign in with your own account.
2. Install/configure your Revit MCP add-in and Node server; enable the service in Revit.
3. Copy `.mcp.example.json` to `.mcp.json` in the app folder; replace its example server path. Alternatively set `CASEGRAPH_MCP_CONFIG` to your own JSON file before starting Revit.
4. Release any other client's MCP connection before generating/applying suggestions.

Optional variables: `CASEGRAPH_CLAUDE` (CLI path), `CASEGRAPH_CLAUDE_MODEL` (model), `CASEGRAPH_MCP_CONFIG`. Separate optional analysis can use `CASEGRAPH_LLM_URL`, `CASEGRAPH_LLM_MODEL`, `CASEGRAPH_LLM_KEY`. No API keys are supplied. Never distribute configured MCP/environment files or credentials. AI features send relevant model evidence to the provider you configure.

## Data, updates and uninstall

Cases/logs are created locally in `backend/data` after use; `CASEBASE_DATA_DIR` can override the backend data directory. No database, models, screenshots, personal logs, account configuration or API keys are shipped. Public third-party licenses and author attributions are retained.

Before updating, close Revit and the installed backend. Re-run the installer; case data, local MCP settings and shaft settings are preserved. The prior application remains in a `previous-*` sibling folder for recovery. Data in a separate development workspace is not migrated automatically: back it up, then copy its `backend/data` while both services are stopped.

Use Windows **Installed apps → Case-driven BIM Diagnosis and Repair → Uninstall**. Data/settings remain in a `saved-data-*` sibling folder; previous versions are retained. Delete those backups manually only when no longer needed.

## Troubleshooting

| Symptom | Action |
| --- | --- |
| Missing ribbon or old UI | Confirm pyRevit attachment to Revit 2022, remove duplicate search paths, restart Revit |
| Backend unavailable | Run Start-menu **Check Installation**; read `backend/data/server.log`; port 8000 must be free |
| Manual backend start | Run `powershell -ExecutionPolicy Bypass -File Start-CaseGraph.ps1` in the app folder |
| Claude/MCP 409 | Release the other client's connection in Revit MCP settings, then retry |
| CSV export disabled | Run a fresh shaft check; a report without problems has nothing to export |
| Missing shaft DLL | Reinstall this Revit 2022 package; other Revit versions need matching builds |

Health: `http://127.0.0.1:8000/graph/health`. The backend listens on localhost. **Check Installation** uses a temporary database and makes no AI requests or Revit changes. UI tests simulate Revit documents; validate native geometry/repairs in your own Revit model.

## Development and release build

Keep `CaseBase.extension`, `backend`, `shared` and `ShaftChecker-main` together. The internal package name is retained for compatibility.

```powershell
python -m venv backend/.venv
backend/.venv/Scripts/python -m pip install -r backend/requirements-dev.txt
./tools/Build-ShaftChecker.ps1 -RevitVersion 2022
Set-Location backend
./.venv/Scripts/python -m pytest tests -q
Set-Location ..
powershell -NoProfile -STA -ExecutionPolicy Bypass -File backend/tests/run_ironpython_encoding.ps1 -Suite panel
backend/.venv/Scripts/python tools/build_release.py
```

The release builder uses a fixed runtime, hash-locked wheels and a file allowlist. It downloads missing build inputs, verifies/scans the payload and writes the installer, checksum and audit to `dist`. Exact hashes are in `installer/release-lock.json`. The runtime comes from the [official Python 3.13.17 release](https://www.python.org/downloads/release/python-31317/).
