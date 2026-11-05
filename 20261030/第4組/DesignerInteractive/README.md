# Interactive Parametric Designer — offline WPF candidate

Development branch: `dev/interactive-parametric-designer`. Preserved baseline: `dev/site-poc-two-car`, commit `c2b0df9a05b5e1ff36b63830d416561149e2fbe2`. GitHub implementation tracker: [issue #2](https://github.com/zoehchen/EV_Charging_Hub/issues/2).

This candidate is a standalone WPF UI revision, **not a deployed Revit add-in**. It has no Revit API or RevitMCP dependency. The existing installed generation command still loads the original UI; connecting this candidate to Revit requires a separately approved integration/deployment step.

## Implemented

- Seven collapsible input groups: Basic Design, Parking Layout, Canopy & Structure, Solar & Energy, EV Charging Equipment, Site & Circulation, Visualization.
- Sliders alongside precise numeric inputs and units, including placement rotation. C2 capacity retains 2–1000 points; explicit rows, supports, dimensions, PV and drainage controls are retained. Suggested slider ranges expand to accommodate typed/imported values; only deliberate slider edits are quantized. Empty/nonfinite/fractional integer input is retained with feedback.
- A 180 ms dispatcher debounce recalculates pure previews. Input edits immediately clear stale geometry/quantities and lock generation. It never invokes model preflight, transactions, family placement or model generation.
- Existing 2D plan, projected roof cross-section, design summary, material takeoff, site/canopy dimensions, overhead clearance and rated DC PV capacity.
- Presentation-only PV/roof/structure visibility toggles. These do not recalculate the design, invalidate its fingerprint, change JSON, or alter generation authorization.
- Existing import/export, saved-design selection, create/replace modes, identity handling, finite rotation validation, legacy-rotation review and explicit generation confirmation are retained in the window. Standalone generation is hidden/disabled.

## Supported shapes

| Selection | State | Existing engine mapping |
| --- | --- | --- |
| Low-slope timber / outward — positive fall | Enabled, unchanged geometry/default slope | `DrainageDirection.Outward` |
| Butterfly / inward | Enabled | `DrainageDirection.TowardSpine` |
| Flat (0%) | Disabled, future | Existing `SlopePercent` range is 0.1–10%; 0% is rejected |
| Curved timber arch | Disabled, future | No corresponding generator |
| Wave / freeform | Disabled, future | No corresponding generator |

The shape selection maps existing drainage geometry; it does not add a `Shape` property or change schema-1 configuration fingerprints. The original drainage selector is also retained, and the two controls remain synchronized. Roof material selection does not establish structural engineering or create curved geometry.

## Source boundaries

`Presentation/DesignerInteractive.Presentation.csproj` **links**, without editing, the release `CanopyViewModel.cs`, `CanopyPresentation.cs` and `PlannerViewModel.cs`. It references the original `EVCanopyDesigner.Canopy.Core` project, whose Planner/Canopy engines and codec remain authoritative. The interactive facade adds only controls, grouping, edit notifications and presentation preferences. The new UI derives from the existing window/renderer and keeps `ShowForRevit(long, DesignChoice[], Func<GenerationRequest,ValidationReport>)` for a future reviewed integration.

The UI builds with the distinct assembly name `EVCanopyDesigner.Interactive.UI`; do not rename/copy it over installed binaries. No deployment script is included. `GenerateC2CanopyCommand`, `GenerationService`, document/disposable guards, ownership checks, delete allow-lists, rollback and failure-handling code are unchanged.

## Build and use

Use the installed .NET 10 SDK on Windows; no new package, runtime or add-in installation is required.

```powershell
dotnet build DesignerInteractive/UI/DesignerInteractive.UI.csproj -c Release
dotnet run --project DesignerInteractive/Tests -c Release
```

To launch the standalone window after approving GUI execution in your environment:

```powershell
dotnet DesignerInteractive/UI/bin/Release/net10.0-windows/EVCanopyDesigner.Interactive.UI.dll
```

Build and run the actual WPF interaction probe with a **new** output directory:

```powershell
dotnet build DesignerInteractive/UIProbe -c Release
dotnet DesignerInteractive/UIProbe/bin/Release/net10.0-windows/DesignerInteractive.UIProbe.dll DesignerInteractive/artifacts/new-ui-run
```

The probe opens and closes only its standalone test window. Its injected preflight callback is a mock; it never connects to Revit. It exercises real WPF controls/bindings, slider debounce, invalid input recovery, disabled roof items, selected-design restoration, rotation review and compact layout, and writes screenshots. Existing output directories are refused. Earlier local runs are preserved; the committed [final evidence](../docs/previews/InteractiveDesigner-20261025-01/README.md) uses the final 22-check run.

## Remaining limits

Preview calculation runs synchronously on the UI dispatcher after debounce; very large legal configurations may be slower or hit the existing component limits. Navigation ranges are not regulatory/design constraints; the original engine remains the validator. Plan/section views use local coordinates; placement rotation is applied by the existing explicit Revit workflow, not by the preview camera.

Manufacturer-specific charging equipment, power demand, AC output/energy yield, site circulation/turning radii, genuinely flat/curved/wave geometry, accessibility and engineering certification remain unimplemented or unverified. Offline generic station footprints and their conflict checks are implemented below. The two-car SitePoc stays a separate preserved companion. Live Revit acceptance, adapter binding and installation are **not performed** by this phase.

See the [inspection/design and validation record](../docs/progress/2026-10-25-interactive-designer.md) and [PPT tracking](../docs/PPT-IMPLEMENTATION-TRACKING.md).

## Resumed equipment planning

The equipment group now shows approved planning counts from the valid current layout: ceil(spaces/2) physical stations, two installed outputs per station, and exactly spaces assigned simultaneous points. Invalid/pending layouts clear these counts. This informational projection changes no configuration, fingerprint, quantity ledger or generated geometry. See [equipment specification](../docs/CHARGING-EQUIPMENT-SPEC.md) and [new evidence](../docs/previews/InteractiveDesigner-20261025-03/README.md).

## Current phase: offline dual-output station placement

The count-only phase above is historical. `ChargingEquipment.cs` now calculates physical station prisms, service zones, stable S1–S5/output IDs, opposite-row space assignments and assumed 3D cable reach. Fifteen equipment parameters have precise inputs/sliders. The live Plan overlays footprints, service envelopes and cables; Charging plan zooms to parking/canopy bounds with exact space IDs. Equipment shows counts, assignments and blocking diagnostics. PV visibility only changes display.

The unchanged default layout has five stations and ten assigned points, but the central-spine candidate is **CONFLICT**, not an accepted installation. All ten cables pass the configured reach assumption; continuous pedestrian/service access does not. Read the [equipment specification and defaults](../docs/CHARGING-EQUIPMENT-SPEC.md) and [final screenshots/evidence](../docs/previews/DualOutputPlacement-20261025-02/README.md).

Save original canopy JSON with the existing actions, and save equipment separately with Export equipment. Import matching canopy first, then Import equipment. The strict equipment companion includes its own schema/units and canopy-bound fingerprint. Equipment changes never alter canopy fingerprints, quantities or SitePoc assets. Conflict-free numeric input is not proof of feasible placement; exported conflicting configurations are review artifacts. Generation remains disabled even through the retained future window entry-point.

Current validation: 31 Designer regressions; 32 actual WPF checks; existing Canopy 65, Planner 24, Core 14 and SitePoc 70. Release UI/probe build: zero warnings/errors. Positive-slope Outward is labeled low-slope timber with positive fall; true 0% Flat stays disabled. Station geometry is currently displayed in plan, not the projected canopy cross-section or a 3D/Revit view. This phase does not add a Revit equipment adapter.

## Controlled integration preparation

A separate, Revit-free [Integration project](Integration/README.md) now provides canopy-only scope contracts, an inert read-only host adapter and single-use preflight tickets. It is not wired into this UI or the production command; generation stays disabled. Equipment/combined-site generation always rejects and the 21 physical-placement conflicts remain unchanged. See [architecture, implementation plan and disposable-model test sequence](../docs/REVIT-INTEGRATION-READINESS.md). Twenty integration tests pass; actual Revit/runtime and model acceptance remain pending.

## Completed controlled Revit POC — current status

The preceding sections document earlier offline stages. The scoped WPF UI is now connected to a **separate side-by-side Revit candidate**, using the Integration contracts and unchanged baseline transaction services. The production command/add-in is untouched. Actual Revit acceptance passed 16 checks, including generation, selected replacement, real rollback, cancellation and stale-level rejection in a fresh certified disposable. Standalone and the retained unscoped entry-point still cannot generate. Equipment/combined-site generation stays disabled with all 21 conflicts retained.

See [consolidated report and exact current model/package paths](../docs/REVIT-INTERACTIVE-POC-REPORT.md), [actual Revit images/results](../docs/previews/RevitInteractivePOC-20261025-01/README.md) and [host instructions](RevitHost/README.md). This is a controlled canopy-only POC, not approval of the equipment/site or verification of the historical two-car SitePoc in Revit.
