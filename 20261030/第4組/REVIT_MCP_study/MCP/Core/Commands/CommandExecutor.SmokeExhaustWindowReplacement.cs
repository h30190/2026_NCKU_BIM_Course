using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.DB.Structure;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    public partial class CommandExecutor
    {
        private sealed class ReplacementParameterValue
        {
            public string Name { get; set; }
            public IdType ParameterId { get; set; }
            public string BuiltInParameter { get; set; }
            public StorageType StorageType { get; set; }
            public bool IsReadOnly { get; set; }
            public bool HasValue { get; set; }
            public string Value { get; set; }
            public bool IsBuiltIn { get; set; }
            public bool IsSharedParameter { get; set; }
            public string SharedParameterGuid { get; set; }
            public bool IsProjectParameter { get; set; }
            public bool IsFamilyDefinedParameter { get; set; }
            public bool IsTypeDrivenOrReadOnlyReflected { get; set; }
            public string SafetyClassification { get; set; }
            public IdType AssociatedGlobalParameterId { get; set; }
            public string AssociatedGlobalParameterName { get; set; }
            public string GlobalAssociationReadError { get; set; }
        }

        private sealed class ReplacementWindowSnapshot
        {
            public IdType ElementId { get; set; }
            public IdType TypeId { get; set; }
            public string Family { get; set; }
            public string TypeName { get; set; }
            public IdType HostWallId { get; set; }
            public double LocationX { get; set; }
            public double LocationY { get; set; }
            public double LocationZ { get; set; }
            public double SillHeightMm { get; set; }
            public double HeadHeightMm { get; set; }
            public double FacingX { get; set; }
            public double FacingY { get; set; }
            public double FacingZ { get; set; }
            public double HandX { get; set; }
            public double HandY { get; set; }
            public double HandZ { get; set; }
            public List<ReplacementParameterValue> Parameters { get; set; } = new List<ReplacementParameterValue>();
            public double HeightInZone { get; set; }
            public double AreaInZone { get; set; }
            public double OpeningRatio { get; set; }
            public double EffectiveArea { get; set; }
        }

        private sealed class StoredWindowReplacementPlan
        {
            public string PlanId { get; set; }
            public DateTime CreatedUtc { get; set; }
            public string DocumentKey { get; set; }
            public IdType RoomId { get; set; }
            public IdType TargetTypeId { get; set; }
            public string TargetFamily { get; set; }
            public string TargetTypeName { get; set; }
            public double TargetWidthMm { get; set; }
            public double TargetHeightMm { get; set; }
            public double TargetSillMm { get; set; }
            public double CeilingHeightMm { get; set; }
            public double ZoneBottomMm { get; set; }
            public double ZoneTopMm { get; set; }
            public double RequiredArea { get; set; }
            public double CurrentTotal { get; set; }
            public double PredictedTotal { get; set; }
            public string PredictedResult { get; set; }
            public bool ReplacementSafe { get; set; }
            public List<string> Warnings { get; set; }
            public List<string> BlockingWarnings { get; set; }
            public List<string> InformationalWarnings { get; set; }
            public List<ReplacementWindowSnapshot> Sources { get; set; }
            public List<ReplacementWindowSnapshot> Proposed { get; set; }
        }

        private sealed class StoredWindowReplacementOperation
        {
            public string OperationId { get; set; }
            public string DocumentKey { get; set; }
            public IdType RoomId { get; set; }
            public IdType TargetTypeId { get; set; }
            public double TargetSillMm { get; set; }
            public List<ReplacementWindowSnapshot> Sources { get; set; }
            public List<IdType> CreatedIds { get; set; }
        }

        private sealed class ReplacementFamilyParameterInfo
        {
            public StorageType StorageType { get; set; }
            public bool IsInstance { get; set; }
            public string DataTypeId { get; set; }
            public bool IsShared { get; set; }
            public string Guid { get; set; }
        }

        private static readonly object WindowReplacementLock = new object();
        private static readonly Dictionary<string, StoredWindowReplacementPlan> WindowReplacementPlans = new Dictionary<string, StoredWindowReplacementPlan>();
        private static readonly Dictionary<string, StoredWindowReplacementOperation> WindowReplacementOperations = new Dictionary<string, StoredWindowReplacementOperation>();
        private const double ReplacementMmPerFoot = 304.8;

        private object PlanSmokeExhaustWindowReplacement(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType roomId = parameters["roomId"]?.Value<IdType>() ?? 0;
            string requestedType = parameters["targetWindowType"]?.Value<string>();
            double sillMm = parameters["targetSillHeightMm"]?.Value<double>() ?? double.NaN;
            int requiredCount = parameters["requiredCount"]?.Value<int>() ?? 0;
            if (roomId == 0 || string.IsNullOrWhiteSpace(requestedType) || requiredCount < 1)
                throw new Exception("roomId, targetWindowType, and requiredCount >= 1 are required.");
            if (!IsFinite(sillMm) || sillMm < 0) throw new Exception("targetSillHeightMm must be a finite non-negative number.");
            Room room = doc.GetElement(roomId.ToElementId()) as Room;
            if (room == null || room.Area <= 0) throw new Exception($"Room {roomId} was not found or is not placed.");
            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("The Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject { ["levelName"] = level.Name, ["colorize"] = false }));
            JObject checkedRoom = (checker["Rooms"] as JArray)?.OfType<JObject>().FirstOrDefault(x => x["RoomId"]?.Value<IdType>() == roomId);
            if (checkedRoom == null) throw new Exception("The smoke exhaust checker did not return the selected Room.");
            JObject summary = checkedRoom["Summary"] as JObject ?? new JObject();
            double requiredArea = summary["RequiredArea"]?.Value<double>() ?? Math.Round(room.Area * 0.092903 * 0.02, 4);
            double currentTotal = summary["TotalEffectiveArea"]?.Value<double>() ?? 0;
            double ceiling = checkedRoom["SmokeZoneTop"]?.Value<double>() ?? GetCeilingHeight(doc, room, "room_parameter");
            double zoneTop = checkedRoom["SmokeZoneTop"]?.Value<double>() ?? ceiling;
            double zoneBottom = checkedRoom["SmokeZoneBottom"]?.Value<double>() ?? zoneTop - 800;

            string familyName = requestedType.Contains(":") ? requestedType.Substring(0, requestedType.IndexOf(':')).Trim() : string.Empty;
            string typeName = requestedType.Contains(":") ? requestedType.Substring(requestedType.IndexOf(':') + 1).Trim() : requestedType.Trim();
            FamilySymbol target = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(s => s.Category?.Id.GetIdValue() == (IdType)(int)BuiltInCategory.OST_Windows &&
                    string.Equals(s.Name, typeName, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(familyName) || string.Equals(s.FamilyName, familyName, StringComparison.OrdinalIgnoreCase)));
            if (target == null) throw new Exception($"Loaded Window Type '{requestedType}' was not found or is ambiguous.");
            familyName = target.FamilyName;
            double widthMm = GetSmokeWindowDimensionMm(target, true), heightMm = GetSmokeWindowDimensionMm(target, false);
            double headMm = sillMm + heightMm;
            double openingRatio = GetWindowOperationType(familyName, typeName).openingRatio;
            if (openingRatio <= 0) throw new Exception("The target Window Type is not classified as operable by the smoke checker.");
            if (headMm > ceiling + 0.01) throw new Exception($"Target HeadHeight {headMm:F1} mm exceeds CeilingHeight {ceiling:F1} mm.");

            JArray checkerRows = checkedRoom["Windows"] as JArray ?? new JArray();
            var fixedSources = new List<ReplacementWindowSnapshot>();
            foreach (JObject row in checkerRows.OfType<JObject>())
            {
                if (row["OpeningRatio"]?.Value<double>() != 0 || !string.Equals(row["OperationType"]?.Value<string>(), "fixed", StringComparison.OrdinalIgnoreCase)) continue;
                IdType id = row["WindowId"]?.Value<IdType>() ?? 0;
                FamilyInstance instance = doc.GetElement(id.ToElementId()) as FamilyInstance;
                if (instance == null || instance.Host == null) continue;
                fixedSources.Add(CaptureReplacementWindowSnapshot(instance, row));
            }
            if (fixedSources.Count < requiredCount) throw new Exception($"Room contains only {fixedSources.Count} checker-confirmed fixed Windows; {requiredCount} requested.");

            // Pick the best spaced set of sources on one host. Existing non-selected windows
            // remain in place and are included in the collision clearances.
            var candidates = new List<Tuple<List<ReplacementWindowSnapshot>, double>>();
            foreach (var group in fixedSources.GroupBy(x => x.HostWallId))
            {
                var ordered = group.OrderBy(x => x.LocationX).ThenBy(x => x.LocationY).ToList();
                foreach (var combo in GetReplacementCombinations(ordered, requiredCount))
                {
                    double minClear = double.PositiveInfinity;
                    bool valid = true;
                    foreach (var source in combo)
                    {
                        Wall candidateHost = doc.GetElement(source.HostWallId.ToElementId()) as Wall;
                        Curve hostCurve = candidateHost?.Location is LocationCurve hostLocation ? hostLocation.Curve : null;
                        if (hostCurve == null) { valid = false; continue; }
                        XYZ start = hostCurve.GetEndPoint(0), end = hostCurve.GetEndPoint(1);
                        double startDistance = Math.Sqrt(Math.Pow(source.LocationX - start.X * ReplacementMmPerFoot, 2) + Math.Pow(source.LocationY - start.Y * ReplacementMmPerFoot, 2));
                        double endDistance = Math.Sqrt(Math.Pow(source.LocationX - end.X * ReplacementMmPerFoot, 2) + Math.Pow(source.LocationY - end.Y * ReplacementMmPerFoot, 2));
                        if (Math.Min(startDistance, endDistance) < widthMm / 2.0 + 50) valid = false;
                        minClear = Math.Min(minClear, Math.Min(startDistance, endDistance) - widthMm / 2.0);
                        foreach (var peer in combo.Where(x => x.ElementId != source.ElementId))
                        {
                            double peerCenterDistance = Math.Sqrt(Math.Pow(source.LocationX - peer.LocationX, 2) + Math.Pow(source.LocationY - peer.LocationY, 2));
                            double peerClear = peerCenterDistance - widthMm;
                            minClear = Math.Min(minClear, peerClear);
                            if (peerClear < 50) valid = false;
                        }
                        foreach (var other in checkerRows.OfType<JObject>())
                        {
                            IdType otherId = other["WindowId"]?.Value<IdType>() ?? 0;
                            if (combo.Any(x => x.ElementId == otherId)) continue;
                            if (other["HostWallId"]?.Value<IdType>() != source.HostWallId) continue;
                            double otherX = other["LocationX"]?.Value<double>() ?? source.LocationX;
                            double otherWidth = other["Width"]?.Value<double>() ?? widthMm;
                            double clear = Math.Abs(source.LocationX - otherX) - (widthMm + otherWidth) / 2.0;
                            minClear = Math.Min(minClear, clear);
                            if (clear < 50) valid = false;
                        }
                    }
                    if (valid) candidates.Add(Tuple.Create(combo, minClear));
                }
            }
            List<ReplacementWindowSnapshot> selected = candidates.OrderByDescending(x => x.Item2)
                .ThenBy(x => x.Item1.Sum(w => w.ElementId)).Select(x => x.Item1).FirstOrDefault();
            if (selected == null) throw new Exception("No source Window set meets the target-width clear-spacing check on a common Host Wall.");

            var blockingWarnings = new List<string>();
            var informationalWarnings = new List<string>();
            bool safe = true;
            Dictionary<string, ReplacementFamilyParameterInfo> targetParameters = ReadReplacementFamilyParameters(doc, target.Family, informationalWarnings);
            if (targetParameters == null)
            {
                blockingWarnings.Add($"Could not inspect target Family '{familyName}' for required Window Inset and Global Parameter compatibility.");
            }
            foreach (ReplacementWindowSnapshot source in selected)
            {
                FamilyInstance live = doc.GetElement(source.ElementId.ToElementId()) as FamilyInstance;
                Dictionary<string, ReplacementFamilyParameterInfo> sourceParameters = ReadReplacementFamilyParameters(doc, live.Symbol.Family, informationalWarnings);
                foreach (ReplacementParameterValue p in source.Parameters)
                {
                    ReplacementFamilyParameterInfo sourceFamilyParameter = null;
                    if (sourceParameters != null) sourceParameters.TryGetValue(p.Name, out sourceFamilyParameter);
                    if (sourceFamilyParameter != null && !sourceFamilyParameter.IsInstance)
                    {
                        p.IsTypeDrivenOrReadOnlyReflected = true;
                        if (p.SafetyClassification == "FamilyDefined") p.SafetyClassification = "TypeControlled";
                    }
                    if (!string.IsNullOrEmpty(p.GlobalAssociationReadError))
                    { blockingWarnings.Add($"Could not read Global Parameter association for '{p.Name}' on source Window {source.ElementId}: {p.GlobalAssociationReadError}"); }

                    if (p.AssociatedGlobalParameterId != 0)
                    {
                        bool gpCompatible = targetParameters != null && targetParameters.TryGetValue(p.Name, out ReplacementFamilyParameterInfo gpTarget) &&
                            sourceFamilyParameter != null && gpTarget.IsInstance && sourceFamilyParameter.IsInstance &&
                            gpTarget.StorageType == sourceFamilyParameter.StorageType &&
                            string.Equals(gpTarget.DataTypeId, sourceFamilyParameter.DataTypeId, StringComparison.Ordinal) &&
                            (!sourceFamilyParameter.IsShared || (gpTarget.IsShared && string.Equals(gpTarget.Guid, sourceFamilyParameter.Guid, StringComparison.OrdinalIgnoreCase)));
                        if (!gpCompatible)
                            blockingWarnings.Add($"Global Parameter association on '{p.Name}' (Id {p.AssociatedGlobalParameterId}) has no compatible instance parameter in target Family '{familyName}' for source Window {source.ElementId}.");
                        continue;
                    }

                    if (p.IsSharedParameter || p.IsProjectParameter)
                    {
                        bool valued = p.HasValue && !string.IsNullOrWhiteSpace(p.Value) && p.Value != "-1";
                        if (valued && !IsReplacementParameterAvailableOnTarget(doc, target, p, targetParameters))
                            blockingWarnings.Add($"Valued {(p.IsSharedParameter ? "Shared" : "Project")} parameter '{p.Name}' cannot be restored on replacement Window {source.ElementId}.");
                        else if (valued)
                            informationalWarnings.Add($"Valued {(p.IsSharedParameter ? "Shared" : "Project")} parameter '{p.Name}' on source Window {source.ElementId} is restorable by its parameter identity.");
                        continue;
                    }

                    if (p.IsReadOnly || p.IsTypeDrivenOrReadOnlyReflected || p.SafetyClassification == "SystemOrDerived" || p.SafetyClassification == "ElementIdentity")
                        informationalWarnings.Add($"'{p.Name}' on source Window {source.ElementId} is {p.SafetyClassification}; it is not required to match a FamilyManager parameter.");
                    else if (p.HasValue && !string.IsNullOrWhiteSpace(p.Value) && !IsReplacementParameterAvailableOnTarget(doc, target, p, targetParameters))
                        blockingWarnings.Add($"Writable valued instance parameter '{p.Name}' cannot be restored on replacement Window {source.ElementId}.");
                }
                ReplacementParameterValue sourceInset = source.Parameters.FirstOrDefault(x => x.Name.Equals("Window Inset", StringComparison.OrdinalIgnoreCase));
                if (sourceInset != null)
                {
                    ReplacementFamilyParameterInfo sourceInsetFamilyParameter = null;
                    if (sourceParameters != null) sourceParameters.TryGetValue("Window Inset", out sourceInsetFamilyParameter);
                    bool insetCompatible = targetParameters != null && targetParameters.TryGetValue("Window Inset", out ReplacementFamilyParameterInfo targetInset) &&
                        sourceInsetFamilyParameter != null && sourceInsetFamilyParameter.StorageType == targetInset.StorageType &&
                        sourceInsetFamilyParameter.IsInstance && targetInset.IsInstance &&
                        string.Equals(sourceInsetFamilyParameter.DataTypeId, targetInset.DataTypeId, StringComparison.Ordinal) &&
                        (!sourceInsetFamilyParameter.IsShared || (targetInset.IsShared && string.Equals(sourceInsetFamilyParameter.Guid, targetInset.Guid, StringComparison.OrdinalIgnoreCase)));
                    if (!insetCompatible) blockingWarnings.Add($"Window Inset is present on source Window {source.ElementId} but is missing or incompatible in target Family '{familyName}'.");
                }
            }
            safe = blockingWarnings.Count == 0;

            double areaInZone = widthMm / 1000.0 * Math.Max(0, Math.Min(headMm, zoneTop) - Math.Max(sillMm, zoneBottom)) / 1000.0;
            double perWindowEffective = Math.Round(areaInZone * openingRatio, 4);
            double predicted = Math.Round(currentTotal - selected.Sum(x => Math.Round(x.EffectiveArea, 4)) + perWindowEffective * selected.Count, 4);
            string predictedResult = predicted >= requiredArea ? "PASS" : "FAIL";
            var proposed = selected.Select(x => new ReplacementWindowSnapshot
            {
                ElementId = x.ElementId, TypeId = target.Id.GetIdValue(), Family = familyName, TypeName = typeName,
                HostWallId = x.HostWallId, LocationX = x.LocationX, LocationY = x.LocationY,
                LocationZ = x.LocationZ + (sillMm - x.SillHeightMm), SillHeightMm = sillMm, HeadHeightMm = headMm,
                FacingX = x.FacingX, FacingY = x.FacingY, FacingZ = x.FacingZ, HandX = x.HandX, HandY = x.HandY, HandZ = x.HandZ,
                HeightInZone = Math.Max(0, Math.Min(headMm, zoneTop) - Math.Max(sillMm, zoneBottom)),
                AreaInZone = areaInZone, OpeningRatio = openingRatio, EffectiveArea = perWindowEffective
            }).ToList();
            string planId = Guid.NewGuid().ToString("N");
            var plan = new StoredWindowReplacementPlan
            {
                PlanId = planId, CreatedUtc = DateTime.UtcNow, DocumentKey = GetSmokePlanDocumentKey(doc), RoomId = roomId,
                TargetTypeId = target.Id.GetIdValue(), TargetFamily = familyName, TargetTypeName = typeName,
                TargetWidthMm = widthMm, TargetHeightMm = heightMm, TargetSillMm = sillMm, CeilingHeightMm = ceiling,
                ZoneBottomMm = zoneBottom, ZoneTopMm = zoneTop, RequiredArea = requiredArea, CurrentTotal = currentTotal,
                PredictedTotal = predicted, PredictedResult = predictedResult, ReplacementSafe = safe,
                Warnings = blockingWarnings.Concat(informationalWarnings).Distinct().ToList(),
                BlockingWarnings = blockingWarnings.Distinct().ToList(), InformationalWarnings = informationalWarnings.Distinct().ToList(),
                Sources = selected, Proposed = proposed
            };
            lock (WindowReplacementLock) WindowReplacementPlans[planId] = plan;
            return new
            {
                Success = true, PlanId = planId, RoomId = roomId,
                SelectedSourceWindowIds = selected.Select(x => x.ElementId).ToList(), SourceSnapshots = selected,
                TargetType = new { Family = familyName, TypeName = typeName, TypeId = target.Id.GetIdValue(), WidthMm = widthMm, HeightMm = heightMm, OpeningRatio = openingRatio },
                ProposedWindows = proposed, ReplacementSafe = safe, ReplacementWarnings = plan.Warnings,
                BlockingWarnings = plan.BlockingWarnings, InformationalWarnings = plan.InformationalWarnings,
                CurrentTotalEffectiveArea = currentTotal, TotalEffectiveArea = predicted, RequiredArea = requiredArea,
                Deficit = Math.Max(0, requiredArea - predicted), PredictedResult = predictedResult,
                CanApply = safe && predictedResult == "PASS"
            };
        }

        private object ApplySmokeExhaustWindowReplacement(JObject parameters)
        {
            string planId = parameters["planId"]?.Value<string>();
            StoredWindowReplacementPlan plan;
            lock (WindowReplacementLock)
                if (string.IsNullOrWhiteSpace(planId) || !WindowReplacementPlans.TryGetValue(planId, out plan)) throw new Exception("A valid replacement planId is required.");
            if (!plan.ReplacementSafe || plan.PredictedResult != "PASS") throw new Exception("Replacement is blocked because it is unsafe or does not predict PASS.");
            if (DateTime.UtcNow - plan.CreatedUtc > TimeSpan.FromMinutes(30)) throw new Exception("The replacement plan expired; create a fresh plan.");
            Document doc = _uiApp.ActiveUIDocument.Document;
            if (plan.DocumentKey != GetSmokePlanDocumentKey(doc)) throw new Exception("The plan belongs to a different Revit document.");
            Room room = doc.GetElement(plan.RoomId.ToElementId()) as Room;
            FamilySymbol target = doc.GetElement(plan.TargetTypeId.ToElementId()) as FamilySymbol;
            if (room == null || target == null) throw new Exception("The target Room or Window Type no longer exists.");
            ValidateReplacementSources(doc, plan, false);
            var createdIds = new List<IdType>();
            JObject checkerSummary;
            using (Transaction tx = new Transaction(doc, "Replace Fixed Smoke Exhaust Windows"))
            {
                tx.Start();
                try
                {
                    if (!target.IsActive)
                    {
                        target.Activate();
                        doc.Regenerate();
                    }

                    foreach (ReplacementWindowSnapshot snap in plan.Sources)
                    {
                        FamilyInstance source = doc.GetElement(snap.ElementId.ToElementId()) as FamilyInstance;
                        ValidateReplacementSnapshot(source, snap);
                        Wall host = doc.GetElement(snap.HostWallId.ToElementId()) as Wall;
                        if (host == null) throw new Exception($"Host Wall {snap.HostWallId} no longer exists.");
                        XYZ point = new XYZ(snap.LocationX / ReplacementMmPerFoot, snap.LocationY / ReplacementMmPerFoot, snap.LocationZ / ReplacementMmPerFoot);
                        doc.Delete(source.Id);
                        FamilyInstance replacement = doc.Create.NewFamilyInstance(point, target, host, StructuralType.NonStructural);
                        if (replacement == null) throw new Exception($"Revit failed to create replacement for Window {snap.ElementId}.");
                        createdIds.Add(replacement.Id.GetIdValue());
                        RestoreReplacementParameters(replacement, snap, plan.TargetSillMm, restoreAssociations: true);
                        doc.Regenerate();
                        ValidateReplacementGeometry(replacement, snap, plan);
                    }
                    EnsureSmokeRetrofitCheckerResult(doc, room, mustPass: true);
                    checkerSummary = GetReplacementRoomCheckerSummary(doc, room);
                    if (tx.Commit() != TransactionStatus.Committed) throw new Exception("Replacement transaction did not commit.");
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }
            string operationId = Guid.NewGuid().ToString("N");
            lock (WindowReplacementLock)
            {
                WindowReplacementOperations[operationId] = new StoredWindowReplacementOperation
                { OperationId = operationId, DocumentKey = plan.DocumentKey, RoomId = plan.RoomId, TargetTypeId = plan.TargetTypeId,
                    TargetSillMm = plan.TargetSillMm, Sources = plan.Sources, CreatedIds = createdIds };
                WindowReplacementPlans.Remove(plan.PlanId);
            }
            return new { Success = true, OperationId = operationId, RoomId = plan.RoomId,
                DeletedSourceElementIds = plan.Sources.Select(x => x.ElementId).ToList(), CreatedReplacementElementIds = createdIds,
                CheckerResult = checkerSummary };
        }

        private object UndoSmokeExhaustWindowReplacement(JObject parameters)
        {
            string operationId = parameters["operationId"]?.Value<string>();
            StoredWindowReplacementOperation op;
            lock (WindowReplacementLock)
                if (string.IsNullOrWhiteSpace(operationId) || !WindowReplacementOperations.TryGetValue(operationId, out op)) throw new Exception("The replacement operation was not found.");
            Document doc = _uiApp.ActiveUIDocument.Document;
            if (op.DocumentKey != GetSmokePlanDocumentKey(doc)) throw new Exception("The operation belongs to a different Revit document.");
            Room room = doc.GetElement(op.RoomId.ToElementId()) as Room;
            if (room == null) throw new Exception("The original Room no longer exists.");
            foreach (IdType id in op.CreatedIds)
            {
                FamilyInstance item = doc.GetElement(id.ToElementId()) as FamilyInstance;
                if (item == null || item.Symbol.Id.GetIdValue() != op.TargetTypeId) throw new Exception($"Replacement Window {id} is missing or changed.");
            }
            JObject checkerSummary;
            using (Transaction tx = new Transaction(doc, "Undo Fixed Smoke Exhaust Window Replacement"))
            {
                tx.Start();
                try
                {
                    foreach (IdType id in op.CreatedIds) doc.Delete(id.ToElementId());
                    foreach (ReplacementWindowSnapshot snap in op.Sources)
                    {
                        FamilySymbol originalType = doc.GetElement(snap.TypeId.ToElementId()) as FamilySymbol;
                        Wall host = doc.GetElement(snap.HostWallId.ToElementId()) as Wall;
                        if (originalType == null || host == null) throw new Exception($"Original Type or Host for Window {snap.ElementId} no longer exists.");
                        XYZ point = new XYZ(snap.LocationX / ReplacementMmPerFoot, snap.LocationY / ReplacementMmPerFoot, snap.LocationZ / ReplacementMmPerFoot);
                        FamilyInstance restored = doc.Create.NewFamilyInstance(point, originalType, host, StructuralType.NonStructural);
                        if (restored == null) throw new Exception($"Revit failed to restore Window {snap.ElementId}.");
                        RestoreReplacementParameters(restored, snap, snap.SillHeightMm, restoreAssociations: true);
                        RestoreReplacementOrientation(restored, snap);
                        doc.Regenerate();
                        ValidateReplacementSnapshotValues(restored, snap);
                    }
                    EnsureOriginalReplacementCheckerState(doc, room);
                    checkerSummary = GetReplacementRoomCheckerSummary(doc, room);
                    if (tx.Commit() != TransactionStatus.Committed) throw new Exception("Undo transaction did not commit.");
                }
                catch
                {
                    if (tx.GetStatus() == TransactionStatus.Started) tx.RollBack();
                    throw;
                }
            }
            lock (WindowReplacementLock) WindowReplacementOperations.Remove(operationId);
            return new { Success = true, OperationId = operationId, RestoredSourceWindows = op.Sources.Select(x => x.ElementId).ToList(), Recheck = checkerSummary };
        }

        private ReplacementWindowSnapshot CaptureReplacementWindowSnapshot(FamilyInstance window, JObject checkerRow)
        {
            LocationPoint point = window.Location as LocationPoint;
            if (point == null || !(window.Host is Wall)) throw new Exception($"Window {window.Id.GetIdValue()} has no point location or Wall host.");
            var snap = new ReplacementWindowSnapshot
            {
                ElementId = window.Id.GetIdValue(), TypeId = window.Symbol.Id.GetIdValue(), Family = window.Symbol.FamilyName, TypeName = window.Symbol.Name,
                HostWallId = window.Host.Id.GetIdValue(), LocationX = point.Point.X * ReplacementMmPerFoot,
                LocationY = point.Point.Y * ReplacementMmPerFoot, LocationZ = point.Point.Z * ReplacementMmPerFoot,
                SillHeightMm = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM }, new[] { "Sill Height" }) ?? 0) * ReplacementMmPerFoot,
                HeadHeightMm = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM }, new[] { "Head Height" }) ?? 0) * ReplacementMmPerFoot,
                FacingX = window.FacingOrientation.X, FacingY = window.FacingOrientation.Y, FacingZ = window.FacingOrientation.Z,
                HandX = window.HandOrientation.X, HandY = window.HandOrientation.Y, HandZ = window.HandOrientation.Z,
                HeightInZone = checkerRow["HeightInZone"]?.Value<double>() ?? 0,
                AreaInZone = checkerRow["AreaInZone"]?.Value<double>() ?? 0,
                OpeningRatio = checkerRow["OpeningRatio"]?.Value<double>() ?? 0,
                EffectiveArea = checkerRow["EffectiveArea"]?.Value<double>() ?? 0
            };
            foreach (Parameter p in window.Parameters)
            {
                if (p?.Definition == null) continue;
                IdType gpId = 0; string gpName = null; string gpError = null;
                try { ElementId gid = p.GetAssociatedGlobalParameter(); if (gid != null && gid != ElementId.InvalidElementId) { gpId = gid.GetIdValue(); gpName = window.Document.GetElement(gid)?.Name; } }
                catch (Exception ex) { gpError = ex.Message; }
                string value = ReadReplacementParameterValue(p);
                IdType rawId = p.Id.GetIdValue();
                object builtInValue = Enum.ToObject(typeof(BuiltInParameter), rawId);
                bool isBuiltIn = rawId < 0 && Enum.IsDefined(typeof(BuiltInParameter), builtInValue);
                string builtInName = isBuiltIn ? Enum.GetName(typeof(BuiltInParameter), builtInValue) : null;
                bool isShared = false; string sharedGuid = null;
                try { isShared = p.IsShared; if (isShared) sharedGuid = p.GUID.ToString("D"); } catch { }
                bool isProject = !isBuiltIn && !isShared && window.Document.GetElement(p.Id) is ParameterElement;
                bool isFamilyDefined = !isBuiltIn && !isShared && !isProject;
                bool typeDriven = p.IsReadOnly;
                bool identity = string.Equals(builtInName, nameof(BuiltInParameter.IFC_GUID), StringComparison.OrdinalIgnoreCase) ||
                    p.Definition.Name.IndexOf("GUID", StringComparison.OrdinalIgnoreCase) >= 0;
                bool systemOrDerived = typeDriven || new[] { "PHASE_CREATED", "PHASE_DEMOLISHED", "LEVEL_PARAM", "INSTANCE_HEAD_HEIGHT_PARAM", "INSTANCE_SILL_HEIGHT_PARAM" }
                    .Contains(builtInName, StringComparer.OrdinalIgnoreCase);
                string classification = identity ? "ElementIdentity" : systemOrDerived ? "SystemOrDerived" : isProject ? "ProjectParameter" : isShared ? "SharedParameter" : isBuiltIn ? "BuiltInInstance" : isFamilyDefined ? "FamilyDefined" : "Unknown";
                snap.Parameters.Add(new ReplacementParameterValue { Name = p.Definition.Name, ParameterId = p.Id.GetIdValue(), BuiltInParameter = builtInName,
                    StorageType = p.StorageType, IsReadOnly = p.IsReadOnly, HasValue = p.HasValue, Value = value,
                    IsBuiltIn = isBuiltIn, IsSharedParameter = isShared, SharedParameterGuid = sharedGuid, IsProjectParameter = isProject,
                    IsFamilyDefinedParameter = isFamilyDefined, IsTypeDrivenOrReadOnlyReflected = typeDriven, SafetyClassification = classification,
                    AssociatedGlobalParameterId = gpId, AssociatedGlobalParameterName = gpName, GlobalAssociationReadError = gpError });
            }
            return snap;
        }

        private string ReadReplacementParameterValue(Parameter p)
        {
            switch (p.StorageType)
            {
                case StorageType.Double: return p.AsDouble().ToString("R", CultureInfo.InvariantCulture);
                case StorageType.Integer: return p.AsInteger().ToString(CultureInfo.InvariantCulture);
                case StorageType.String: return p.AsString() ?? string.Empty;
                case StorageType.ElementId: return p.AsElementId().GetIdValue().ToString(CultureInfo.InvariantCulture);
                default: return string.Empty;
            }
        }

        private bool CanAssociateReplacementGlobalParameter(Parameter parameter, IdType globalParameterId)
        {
            try { return parameter.CanBeAssociatedWithGlobalParameter(globalParameterId.ToElementId()); }
            catch { return false; }
        }

        private Dictionary<string, ReplacementFamilyParameterInfo> ReadReplacementFamilyParameters(Document projectDoc, Family family, List<string> warnings)
        {
            Document familyDoc = null;
            try
            {
                familyDoc = projectDoc.EditFamily(family);
                if (familyDoc?.FamilyManager == null) return null;
                var result = new Dictionary<string, ReplacementFamilyParameterInfo>(StringComparer.OrdinalIgnoreCase);
                foreach (FamilyParameter parameter in familyDoc.FamilyManager.Parameters)
                {
                    if (parameter?.Definition == null) continue;
                    string dataTypeId = null;
                    try { dataTypeId = parameter.Definition.GetDataType()?.TypeId; } catch { }
                    result[parameter.Definition.Name] = new ReplacementFamilyParameterInfo
                    { StorageType = parameter.StorageType, IsInstance = parameter.IsInstance, DataTypeId = dataTypeId,
                        IsShared = parameter.IsShared, Guid = parameter.IsShared ? parameter.GUID.ToString("D") : null };
                }
                return result;
            }
            catch (Exception ex)
            {
                warnings.Add($"Could not inspect Family parameters for '{family?.Name}': {ex.Message}");
                return null;
            }
            finally
            {
                try { familyDoc?.Close(false); } catch { }
            }
        }

        private void RestoreReplacementParameters(FamilyInstance target, ReplacementWindowSnapshot snap, double sillMm, bool restoreAssociations)
        {
            foreach (ReplacementParameterValue saved in snap.Parameters)
            {
                Parameter p = ResolveReplacementParameter(target, saved);
                if (p == null || p.StorageType != saved.StorageType)
                {
                    bool requiredValue = saved.HasValue && !string.IsNullOrWhiteSpace(saved.Value) &&
                        (saved.IsSharedParameter || saved.IsProjectParameter || (!saved.IsReadOnly && !saved.IsTypeDrivenOrReadOnlyReflected));
                    if (saved.AssociatedGlobalParameterId != 0 || saved.Name == "Window Inset" || requiredValue)
                        throw new Exception($"Cannot preserve required parameter '{saved.Name}' for Window {snap.ElementId}.");
                    continue;
                }
                if (saved.AssociatedGlobalParameterId != 0 && restoreAssociations)
                {
                    if (!CanAssociateReplacementGlobalParameter(p, saved.AssociatedGlobalParameterId)) throw new Exception($"Cannot restore Global Parameter association for '{saved.Name}'.");
                    p.AssociateWithGlobalParameter(saved.AssociatedGlobalParameterId.ToElementId());
                    if (p.GetAssociatedGlobalParameter()?.GetIdValue() != saved.AssociatedGlobalParameterId)
                        throw new Exception($"Global Parameter association for '{saved.Name}' did not restore to Id {saved.AssociatedGlobalParameterId}.");
                    continue;
                }
                if (saved.BuiltInParameter == nameof(Autodesk.Revit.DB.BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM) || saved.Name.Equals("Sill Height", StringComparison.OrdinalIgnoreCase)) continue;
                if (p.IsReadOnly) continue;
                switch (saved.StorageType)
                {
                    case StorageType.Double: p.Set(double.Parse(saved.Value, CultureInfo.InvariantCulture)); break;
                    case StorageType.Integer: p.Set(int.Parse(saved.Value, CultureInfo.InvariantCulture)); break;
                    case StorageType.String: p.Set(saved.Value); break;
                    case StorageType.ElementId:
                        IdType id; if (IdType.TryParse(saved.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out id)) p.Set(id.ToElementId()); break;
                }
            }
            Parameter sill = target.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill == null) throw new Exception($"Sill Height is missing for replacement of Window {snap.ElementId}.");
            if (sill.IsReadOnly)
            {
                if (Math.Abs(sill.AsDouble() * ReplacementMmPerFoot - sillMm) > 0.5) throw new Exception($"Global Parameter-driven Sill Height does not match the requested value for Window {snap.ElementId}.");
            }
            else if (!sill.Set(sillMm / ReplacementMmPerFoot)) throw new Exception($"Could not set Sill Height for replacement of Window {snap.ElementId}.");
            RestoreReplacementOrientation(target, snap);
        }

        private void RestoreReplacementOrientation(FamilyInstance target, ReplacementWindowSnapshot snap)
        {
            XYZ facing = new XYZ(snap.FacingX, snap.FacingY, snap.FacingZ), hand = new XYZ(snap.HandX, snap.HandY, snap.HandZ);
            if (target.FacingOrientation.DotProduct(facing) < 0.999999 && target.CanFlipFacing) target.flipFacing();
            if (target.HandOrientation.DotProduct(hand) < 0.999999 && target.CanFlipHand) target.flipHand();
            if (target.FacingOrientation.DotProduct(facing) < 0.999999 || target.HandOrientation.DotProduct(hand) < 0.999999)
                throw new Exception($"Facing/Hand orientation could not be restored for source Window {snap.ElementId}.");
        }

        private void ValidateReplacementGeometry(FamilyInstance replacement, ReplacementWindowSnapshot source, StoredWindowReplacementPlan plan)
        {
            if (replacement.Host?.Id.GetIdValue() != source.HostWallId) throw new Exception($"Replacement for Window {source.ElementId} has a different Host Wall.");
            LocationPoint p = replacement.Location as LocationPoint;
            if (p == null || Math.Abs(p.Point.X * ReplacementMmPerFoot - source.LocationX) > 1 || Math.Abs(p.Point.Y * ReplacementMmPerFoot - source.LocationY) > 1)
                throw new Exception($"Replacement for Window {source.ElementId} moved in XY.");
            double sill = (GetParamValue(replacement, new[] { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM }, new[] { "Sill Height" }) ?? 0) * ReplacementMmPerFoot;
            double head = (GetParamValue(replacement, new[] { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM }, new[] { "Head Height" }) ?? 0) * ReplacementMmPerFoot;
            if (Math.Abs(sill - plan.TargetSillMm) > 0.5 || head > plan.CeilingHeightMm + 0.01) throw new Exception($"Replacement for Window {source.ElementId} has invalid Sill/Head height.");
            ValidateReplacementOrientation(replacement, source);
            ValidateReplacementAssociations(replacement, source);
        }

        private void ValidateReplacementSnapshot(FamilyInstance instance, ReplacementWindowSnapshot snap)
        {
            if (instance == null || instance.Symbol.Id.GetIdValue() != snap.TypeId || instance.Host?.Id.GetIdValue() != snap.HostWallId)
                throw new Exception($"Source Window {snap.ElementId} Type/Host changed after planning.");
            LocationPoint p = instance.Location as LocationPoint;
            if (p == null || Math.Abs(p.Point.X * ReplacementMmPerFoot - snap.LocationX) > 1 || Math.Abs(p.Point.Y * ReplacementMmPerFoot - snap.LocationY) > 1)
                throw new Exception($"Source Window {snap.ElementId} location changed after planning.");
            ValidateReplacementSnapshotValues(instance, snap);
        }

        private void ValidateReplacementSnapshotValues(FamilyInstance instance, ReplacementWindowSnapshot snap)
        {
            foreach (ReplacementParameterValue saved in snap.Parameters)
            {
                Parameter p = ResolveReplacementParameter(instance, saved);
                if (p == null || p.StorageType != saved.StorageType || ReadReplacementParameterValue(p) != saved.Value)
                    throw new Exception($"Window {snap.ElementId} parameter '{saved.Name}' was not restored.");
            }
            ValidateReplacementAssociations(instance, snap);
            ValidateReplacementOrientation(instance, snap);
            Parameter sill = instance.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
            if (sill == null || Math.Abs(sill.AsDouble() * ReplacementMmPerFoot - snap.SillHeightMm) > 0.5) throw new Exception($"Window {snap.ElementId} Sill Height was not restored.");
        }

        private void ValidateReplacementAssociations(FamilyInstance instance, ReplacementWindowSnapshot snap)
        {
            foreach (ReplacementParameterValue saved in snap.Parameters.Where(x => x.AssociatedGlobalParameterId != 0))
            {
                Parameter p = ResolveReplacementParameter(instance, saved);
                if (p == null || p.GetAssociatedGlobalParameter()?.GetIdValue() != saved.AssociatedGlobalParameterId)
                    throw new Exception($"Global Parameter association for '{saved.Name}' was not preserved.");
            }
        }

        private Parameter ResolveReplacementParameter(Element target, ReplacementParameterValue saved)
        {
            if (!string.IsNullOrEmpty(saved.BuiltInParameter) && Enum.TryParse(saved.BuiltInParameter, out BuiltInParameter builtIn))
            {
                Parameter builtInParameter = target.get_Parameter(builtIn);
                if (builtInParameter != null) return builtInParameter;
            }
            if (saved.IsSharedParameter && Guid.TryParse(saved.SharedParameterGuid, out Guid sharedGuid))
            {
                Parameter shared = target.get_Parameter(sharedGuid);
                if (shared != null) return shared;
            }
            if (saved.IsProjectParameter && target.Document.GetElement(saved.ParameterId.ToElementId()) is ParameterElement projectParameter)
            {
                Parameter project = target.get_Parameter(projectParameter.GetDefinition());
                if (project != null) return project;
            }
            return target.LookupParameter(saved.Name);
        }

        private bool IsReplacementParameterAvailableOnTarget(Document doc, FamilySymbol target, ReplacementParameterValue source,
            Dictionary<string, ReplacementFamilyParameterInfo> targetParameters)
        {
            if (source.IsBuiltIn && !string.IsNullOrEmpty(source.BuiltInParameter))
                return Enum.TryParse(source.BuiltInParameter, out BuiltInParameter builtIn) &&
                    target.Category?.Id.GetIdValue() == (IdType)(int)BuiltInCategory.OST_Windows && Enum.IsDefined(typeof(BuiltInParameter), builtIn);
            if (source.IsProjectParameter) return doc.GetElement(source.ParameterId.ToElementId()) is ParameterElement;
            if (source.IsSharedParameter && Guid.TryParse(source.SharedParameterGuid, out Guid sharedGuid))
                return targetParameters != null && targetParameters.Values.Any(x => x.IsShared && Guid.TryParse(x.Guid, out Guid targetGuid) && targetGuid == sharedGuid);
            return targetParameters != null && targetParameters.TryGetValue(source.Name, out ReplacementFamilyParameterInfo info) &&
                info.StorageType == source.StorageType && info.IsInstance;
        }

        private void ValidateReplacementOrientation(FamilyInstance instance, ReplacementWindowSnapshot snap)
        {
            if (instance.FacingOrientation.DotProduct(new XYZ(snap.FacingX, snap.FacingY, snap.FacingZ)) < 0.999999 ||
                instance.HandOrientation.DotProduct(new XYZ(snap.HandX, snap.HandY, snap.HandZ)) < 0.999999)
                throw new Exception($"Facing/Hand orientation changed for Window {snap.ElementId}.");
        }

        private void EnsureOriginalReplacementCheckerState(Document doc, Room room)
        {
            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("Checker revalidation failed: Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject { ["levelName"] = level.Name, ["colorize"] = false }));
            JObject checkedRoom = (checker["Rooms"] as JArray)?.OfType<JObject>().FirstOrDefault(x => x["RoomId"]?.Value<IdType>() == room.Id.GetIdValue());
            JObject summary = checkedRoom?["Summary"] as JObject;
            JArray windows = checkedRoom?["Windows"] as JArray;
            if (summary == null || windows == null || windows.Count != 4 || summary["TotalEffectiveArea"]?.Value<double>() != 0 ||
                summary["Result"]?.Value<string>() != "FAIL" || windows.OfType<JObject>().Any(x => x["OpeningRatio"]?.Value<double>() != 0))
                throw new Exception("Undo checker verification failed: Room must return to four fixed windows, TotalEffectiveArea=0, Result=FAIL.");
        }

        private JObject GetReplacementRoomCheckerSummary(Document doc, Room room)
        {
            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("Checker revalidation failed: Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject { ["levelName"] = level.Name, ["colorize"] = false }));
            JObject checkedRoom = (checker["Rooms"] as JArray)?.OfType<JObject>().FirstOrDefault(x => x["RoomId"]?.Value<IdType>() == room.Id.GetIdValue());
            return checkedRoom?["Summary"] as JObject ?? throw new Exception("Checker revalidation failed: Room Summary is missing.");
        }

        private void ValidateReplacementSources(Document doc, StoredWindowReplacementPlan plan, bool applied)
        {
            foreach (ReplacementWindowSnapshot snap in plan.Sources) ValidateReplacementSnapshot(doc.GetElement(snap.ElementId.ToElementId()) as FamilyInstance, snap);
        }

        private IEnumerable<List<T>> GetReplacementCombinations<T>(IList<T> values, int count)
        {
            var result = new List<List<T>>();
            void Walk(int start, List<T> current)
            {
                if (current.Count == count) { result.Add(new List<T>(current)); return; }
                for (int i = start; i <= values.Count - (count - current.Count); i++) { current.Add(values[i]); Walk(i + 1, current); current.RemoveAt(current.Count - 1); }
            }
            Walk(0, new List<T>()); return result;
        }
    }
}
