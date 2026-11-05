# Generate availability — offline diagnostic draft only

`../Bootstrap/GenerateGateDiagnostics.cs` adds one **TransactionMode.ReadOnly** command, `DiagnoseGenerateAvailability`. It does **not** change `CanGenerate`, normal command registration, host guards, geometry or static fields. It is **not built, packaged, deployed or executed** in this investigation. The existing installed c4072dc3 package remains unchanged. No restart, current-model save/close/reopen or new disposable creation occurred.

There is no installed diagnostic entry point for the six private bootstrap conditions. Existing MCP returns project name/view information, not these cached fields or the active document's full path/UniqueId. UI availability reports only the combined enabled state; journals/certificates establish creation/opening history, not every private runtime value. External PowerShell reflection cannot inspect fields inside the separate Revit process. No debugger, heap dump or code injection was used.

## Intended diagnostic behavior after separate authorization

Build to a **new unique diagnostic assembly name**, never the loaded c4072dc3 DLL path. The provided `.addin.template` is a separate **Command** registration for that new assembly; replace both placeholders with a new assembly's absolute path and unique AddInId. It is deliberately not a deployable manifest. Do not install a duplicate POC2 Application manifest or invoke NEW/Generate for diagnostics.

The helper enumerates **already loaded** assemblies and pins the **original c4072dc3 bootstrap**. It reads that original `Loader`'s private fields with `FieldInfo.GetValue` only. If the original assembly is absent or ambiguous, it reports UNKNOWN instead of creating/copying state. It does not initialize `Host.Value`, invoke original private methods, enable a button, modify availability, set fields, post commands, start transactions, save the model or write diagnostic files. Results are displayed in a TaskDialog with separate **PASS / FAIL / UNKNOWN / ERROR** rows.

Rows cover all six original conditions, active-document presence, native-aware `Document.Equals`, repeated document-getter reference equality, cached/live path and project ID, `IsModified`, design-level presence, and independent comparisons to this run's `created-model.json`. The pinned run is `e46bd3719d664d33af991aba3428bf19`. It also reports load-context identities. This is observation only, not a new generation authority or a substitute for existing guards.

**Hypothesis confirmation pattern:** stored object valid, native-aware `Document.Equals` PASS, matching live/stored/certificate path and project ID PASS, but `ReferenceEquals` FAIL. Without that observation, the wrapper explanation remains a hypothesis. If another clause fails or an exception occurs, report that cause rather than changing reference comparison blindly.

Revit previously dynamically detected a newly added manifest in this environment. A future separately authorized, uniquely named **read-only diagnostic Command** might therefore load into the current session and inspect the original run **without a restart or new model**. This is not guaranteed and has not been attempted. If dynamic loading fails, stop; restarting destroys the original in-memory evidence. Do not restart merely to claim the old state was diagnosed.

## Potential correction — not applied

The installed Revit API XML documents `Document.Equals(object)` as equality of instances representing the same currently open Revit document. `ReferenceEquals` instead compares managed reference identity. If the diagnostic proves a wrapper-only mismatch, replace that one comparison with native-aware document equality, retain explicit valid-object checks, canonical full path and `ProjectInformation.UniqueId`, and retain the creator's certificate/snapshot/runtime/same-session/freshness/preflight checks. A filename or project-name match is insufficient.

Under the documented correction/replacement procedure, Revit must be closed before replacing a candidate manifest. A new corrected bootstrap/host load context has no authorized creation state for the old run. **Restart/candidate replacement therefore requires a new guarded disposable run for generation; reopening or adopting the current RVT is prohibited.** A diagnostic-only observer of the still-loaded original candidate is distinct from replacing its generation gate.
