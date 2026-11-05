# Offline canopy integration boundary

This project provides a scoped, non-executing bridge to the existing canopy request. The isolated candidate WPF/Revit host now uses this bridge; the production command is unchanged. This project itself contains no Autodesk/Revit/MCP dependency, transaction or save interface.

`CanopyIntegrationSession.Prepare(CanopyOnly, request, equipmentOptions, acknowledgeEquipmentExclusion: true)` freezes/verifies canonical canopy data and retains equipment findings. Equipment and combined-site scopes always throw. Preparation invokes no host delegate.

Explicit `Validate(review)` obtains a two-minute, single-use ticket from `IReadOnlyCanopyHost`. The future UI must call `Invalidate()` on every edit/import/mode/identity/rotation change. The future command calls `ConfirmForPlacement(review, ticket, explicitCanopyOnlyConfirmation: true)` after origin selection; this reruns required read-only checks and returns `CanopyOnlyHandoff`. Caller must check the typed scope before passing its legacy canopy request into the unchanged guarded transaction service. Handoff always excludes charging equipment and never approves the site.

`ReadOnlyCanopyHostAdapter` binds a real command's context capture and existing read-only preflight delegate. Fake hosts used by the tests prove the boundary contract, not Revit safety, compatibility or successful model generation. Runtime/document/level context is supplied and verified by the future host adapter; paths alone do not establish approval. No ticket bypasses existing model guards, selected ownership, deletion allow-lists, quantity reconciliation or rollback.

```powershell
dotnet build DesignerInteractive/Integration.Tests -c Release
dotnet run --project DesignerInteractive/Integration.Tests -c Release
```

An optional sole argument after `--` writes test JSON to a **new file**; existing evidence is refused. Full architecture, remaining wiring and exact disposable-model acceptance sequence: [integration readiness](../../docs/REVIT-INTEGRATION-READINESS.md).

Current live verification: [16-check controlled Revit POC](../../docs/REVIT-INTERACTIVE-POC-REPORT.md). The source readiness document records the earlier assessment; current side-by-side deployment does not authorize equipment/site generation.
