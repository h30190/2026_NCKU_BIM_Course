# POC 2 isolated static Revit candidate

**Current installation:** corrected package `Bootstrap/bin/packages/c4072dc3c4e34a2db7464899d06372cd/` is installed via its unique manifest; old `ae121aa9` manifest is archived outside Addins. Exactly one active POC 2 manifest remains. All 14 package hashes and 33 unrelated/protected file comparisons passed. **Revit was not launched; corrected UI behavior and live creation/generation remain unverified.** Do not repeat the old-manifest swap. See [installation receipt and exact UI steps](../../docs/progress/2026-10-26-poc2-corrected-installation.md).

The corrected bootstrap provides no-document availability for NEW and candidate-document availability for Generate. Its focused Release build passed with **0 warnings/errors**, and package/compiled registration checks passed. The earlier package/live-attempt history below is retained for traceability.

**Historical ae121aa9 checkpoint:** installed side-by-side and loaded once, but model generation not reached. The controlled attempt verified package hashes/Revit 26.5.0.55; the later diagnosis found its custom ribbon panel but NEW disabled with no active document. No disposable was created and no generation ran. See [live-attempt report](../../docs/progress/2026-10-26-poc2-live-attempt.md). Existing add-ins, RevitMCP configuration and protected models were unchanged.

## Original candidate package (retained; manifest now archived)

Repository-relative location:

`DesignerInteractive/Poc2Revit/Bootstrap/bin/packages/ae121aa918364f708f9fb0a9ed32699e/`

Absolute path:

`C:\Users\zoe\Desktop\win_desktop_zoe\win_dt_zoe_github\EV_Charging_Hub\DesignerInteractive\Poc2Revit\Bootstrap\bin\packages\ae121aa918364f708f9fb0a9ed32699e`

The package contains a unique bootstrap DLL, isolated host/dependency DLLs, original selected configuration, accepted proposal, final maneuver review, separate-root settings, SHA256 inventory and an `.addin` manifest. During the subsequent live attempt only that uniquely named manifest was copied to the user Revit Addins directory, without overwriting anything; DLLs remain in this package. Binaries live under ignored `bin/`; source, packaging procedure and [smoke receipt](../../docs/previews/POC2GeneratorCandidate-20261026/ae121aa918364f708f9fb0a9ed32699e/offline-smoke.json) are committed. The earlier `8e4255958afa4b56b357f642096faf6e` package was superseded after reference-resolution warnings; use only the package above.

## Rebuild/package offline

From repository root, PowerShell 7 with .NET 10 SDK and Revit 2026 API installed:

```powershell
pwsh -NoProfile -File DesignerInteractive/Poc2Revit/package.ps1
python DesignerInteractive/Poc2Revit/verify-package.py <printed-package-directory> <new-smoke-report.json>
```

Packaging invokes the focused Release build and allocates a unique package GUID; existing package folders are never overwritten. It does not start Revit, install a manifest or touch RVTs. Do not package altered evidence to conceal conflicts. The host pins the accepted configuration/equipment fingerprints and rechecks packaged input hashes before creation/generation. Package hashes protect against accidental changes, not adversarially signed provenance.

Final build: **0 warnings / 0 errors**. Initial build reference warnings were corrected by matching the existing host's externally resolved API references. One smoke run passed **14 checks**: inventory hashes/no unexpected files, no copied Revit/Autodesk binaries, separate test root, manifest/bootstrap identity, compiled dependencies, verified configuration, accepted dimensions/counts, retained failures/land assumption, and byte-identical evidence inputs. It does not load Revit API or exercise transactions. No regression suite ran.

## Commands and reuse

- Application: `EVCanopyDesigner.Poc2.Revit.Bootstrap.Poc2Application`.
- **1. NEW POC2 disposable** → bootstrap `NewDisposable` → host `NewDisposableCommand`.
- **2. Generate static UNVALIDATED site** → bootstrap `GenerateStaticSite` → host `GenerateStaticSiteCommand`.

The bootstrap reuses unchanged `CandidateLoadContext.cs` to isolate candidate EV/Designer dependencies from installed production assemblies, sharing only the Revit API and platform assemblies. The new host references the existing compiled `PocOperations`, `CanopyIntegrationSession` and canopy generation workflow. It does not enable `CombinedSite` in that contract; it performs a separately tagged site stage after the normal canopy-only single-use preflight. Existing shared source/guards and historical SitePoc renderer remain unchanged. There is no autorun, acceptance button, replacement, cleanup or physical charger generation.

## Fresh disposable workflow — future controlled test only

Development originally prohibited deployment/live writes; the user subsequently authorized one controlled demonstration. Its installation completed, but command access blocked creation before any model operation. Follow these safeguards when that UI issue is resolved; do not repeat installation over the existing destination:

1. Preserve the packaged inventory. Load/register only this uniquely named candidate using a separately reviewed procedure; never overwrite an existing installed manifest or DLL. Do not use the older POC 1 acceptance command.
2. Manually close all open models. The candidate refuses to create a project while any Revit document is open; it never closes existing documents for you.
3. Run **NEW POC2 disposable**. The unchanged creation workflow validates Revit 2026/API 26/CLR 10 and the approved workspace, creates a **new metric project**, allocates a GUID directory under `Revit_Test_Models/POC2StaticSite/`, saves without overwrite, and records its unique project identity plus immutable initial snapshot/hash and creation certificate. The fixed guarded filename is `EV_Charging_Hub_Planner_11B_Test.rvt`; only the new GUID path identifies this test. No existing/copy RVT may be adopted or supplied fabricated sidecars.
4. Record the **actual displayed full path** and inspect `created-model.json` in that directory. Expected pattern: `C:\Users\zoe\Desktop\win_desktop_zoe\win_dt_zoe_github\EV_Charging_Hub\Revit_Test_Models\POC2StaticSite\<new-guid>\EV_Charging_Hub_Planner_11B_Test.rvt`. **No such candidate RVT was created during development.**
5. Activate the new model's floor plan without editing/saving model changes. Generate refuses a different project/path, a modified document, changed initial element inventory, existing DirectShapes/DataStorage or a repeated attempt. Freshness checks may conservatively refuse a document changed by another add-in; stop rather than weaken them. Process/session state is intentionally required; a restart cannot adopt a previous disposable.
6. Run **Generate static UNVALIDATED site** once. It revalidates guards and inputs, obtains the existing canopy-only preflight and generates one unchanged 144-component canopy under a new design GUID. The recorded XY origin (3.048, 6.096) m is retained; Z binds to the new plan's actual level. It adds the static site and two views in a separate transaction inside the encompassing transaction group. Any transaction warning/error fails closed and rolls back the group. No legacy schema coercion or MCP `--apply` bypass occurs.
7. Inspect `poc2-site-result.json`: only **CommittedAndSaved** means the group committed and the verified disposable saved. Canopy `generation-*.json` reports an **inner transaction only** and is not proof that the entire site survived rollback. A failed save may leave a committed but unsaved disposable; inspect its receipt and UI, do not rerun generation. Failed attempts require a new separately prepared run, not deletion of receipts.
8. Visually inspect `POC 2 - Conceptual Site 3D` (locked orthographic view) and `POC 2 - Site Plan`. Both should contain the exact warning label and findings. Confirm exactly one canopy, ten spaces, four supports, complete site, road/gates, schematic routes and reservations. Plan-only hides roof/PV/upper structure and includes a canopy footprint; 3D retains the full canopy. Confirm physical chargers absent and site-owned elements have separate tags/receipt IDs. If annotation or geometry API calls fail, stop; these paths are not yet live verified.
9. Manually capture viewports with Win+Shift+S to `docs/previews/POC2StaticRevit-20261026/01-conceptual-site-3d.png` and `02-site-plan.png`, then open/verify both. They remain reserved paths, not existing screenshots.

## Intended generated content

| Content | Representation / ownership |
| --- | --- |
| One canopy + ten parking markings | Unchanged 144-component canopy generator, existing canopy ownership ledger |
| 34.5 × 24.5 m site; adjacent 7 m road | Thin conceptual Generic Model DirectShapes, below the original parking marks |
| Two 6 m gateways; two 6 m aisles; widened side connections | Colored surface/stripe DirectShapes |
| Property boundary, entry/exit/parking route arrows | Colored thin schematic ribbons/triangles; not vehicle bodies or swept envelopes |
| Pedestrian strips, marked crossing | Green/yellow reservation overlays, retaining known conflicts |
| Five future equipment-service zones | Amber reservation overlays only; **no station bodies, cables, connectors or charging equipment** |
| Plan/3D, footprint lines, warnings/gateway labels | New dedicated view-specific elements |
| Materials and site registry | New run-scoped tagged resources; no existing material edits |

Site owner: `EVChargingHub.Poc2.StaticSite/1`; DirectShape application IDs, `EVPOC2-<run>-...` Marks and independent Extensible Storage receipts identify roles, run and configuration. Views, notes, resources and registry are tagged too. No deletion or regeneration API is provided. The final JSON lists site-owned IDs/unique IDs; the canopy retains its separate established ledger and receipt.

## Remaining limits

The source configuration is reconstructed and identity-checked, not a new measured live geometry export. Existing steel-classified support geometry is unchanged despite the timber canopy description. Site generation is static: no engineering road/topography, native civil surfaces, accessibility, terrain or electrical design. Vehicle body poses are not generated, so the route ribbons must not be read as passed maneuver checks.

**Row 1 neighboring parked-car clearance, Row 2 pedestrian clearance/unmarked crossing, unrealistic exit-road alignment and all-space turning uncertainty remain. All 21 equipment-access conflicts remain unresolved; extra 245 m² of land is assumed available.** Every view/model is labeled **“POC 2 — Conceptual site layout / circulation not validated.”** `SiteLayoutApproved=false`; equipment remains disabled.

Offline compilation/package checks and observed bootstrap loading do not demonstrate live annotation compatibility, visibility, Revit warnings, geometry reconciliation, save behavior or transaction-group rollback. Next step is safe zero-document command access to continue the already-authorized bounded pilot, not production deployment, repeated installation or a claim of ready/approved circulation.
