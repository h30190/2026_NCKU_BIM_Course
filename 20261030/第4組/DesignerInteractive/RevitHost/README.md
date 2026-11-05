# Isolated Revit 2026 canopy-only POC

The candidate links the existing generation, factory, ownership, validation and document-guard source without modifying it. `PocOperations` adds actual host context, fresh-model authority, scoped WPF handoff and process-scoped legacy settings. `PocAcceptance` exercises real Revit commits/replacement/cancellation/stale-context/rollback and exports evidence.

Only models created by this candidate in the current Revit session can be modified. Creation uses a new GUID directory and the unchanged guard's required basename, refuses overwrite, archives an immutable initial snapshot and records project UniqueId/path. Existing RVTs/markers cannot be adopted. All scope/ownership/transaction checks remain active. Equipment generation is unavailable and site approval is always false.

Build/package: `../Packaging/Build-Candidate.ps1`. Side-by-side installation is a separate explicit `Deploy-Candidate.ps1` action, creating new GUID-named files only. Bootstrap assemblies have unique package identities; Core/UI dependencies are isolated in a private verified context to avoid production type collisions. No RevitMCP modification is involved.

The current controlled POC passed 16 actual Revit checks on 26.5.0.55 / CLR 10.0.13. [Full report, exact model/package paths, screenshots and limitations](../../docs/REVIT-INTERACTIVE-POC-REPORT.md).

Startup automation creates the new document only. Invoke Run POC acceptance from the ribbon afterward so Revit view changes and selection run in a normal external-command context. For interactive editing, select a plan before Canopy ONLY, acknowledge equipment exclusion, Validate and Continue. No model regeneration happens on input edits.
