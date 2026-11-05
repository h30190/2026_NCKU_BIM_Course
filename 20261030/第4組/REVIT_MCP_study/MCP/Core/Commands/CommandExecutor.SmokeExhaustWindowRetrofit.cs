using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
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
        private sealed class SmokeWindowInstanceParameterValue
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        private sealed class SmokeWindowRetrofitSnapshot
        {
            public IdType WindowId { get; set; }
            public IdType TypeId { get; set; }
            public string FamilyName { get; set; }
            public string TypeName { get; set; }
            public double WidthMm { get; set; }
            public double HeightMm { get; set; }
            public double SillHeightMm { get; set; }
            public double HeadHeightMm { get; set; }
            public IdType HostWallId { get; set; }
            public double FacingX { get; set; }
            public double FacingY { get; set; }
            public double FacingZ { get; set; }
            public double HandX { get; set; }
            public double HandY { get; set; }
            public double HandZ { get; set; }
            public double? WindowInsetInternalValue { get; set; }
            public Dictionary<IdType, IdType> GlobalParameterAssociations { get; set; }
            public Dictionary<IdType, SmokeWindowInstanceParameterValue> InstanceParameterValues { get; set; }
            public double LocationX { get; set; }
            public double LocationY { get; set; }
            public double LocationZ { get; set; }
            public double HeightInZoneMm { get; set; }
            public double AreaInZone { get; set; }
            public double OpeningRatio { get; set; }
            public double EffectiveArea { get; set; }
            public double CheckerEffectiveArea { get; set; }
        }

        private sealed class StoredSmokeWindowRetrofitPlan
        {
            public string PlanId { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public string DocumentKey { get; set; }
            public IdType RoomId { get; set; }
            public IdType SourceTypeId { get; set; }
            public IdType ExistingTargetTypeId { get; set; }
            public string FamilyName { get; set; }
            public string ProposedTypeName { get; set; }
            public double ProposedWidthMm { get; set; }
            public double ProposedHeightMm { get; set; }
            public double TargetSillHeightMm { get; set; }
            public double CeilingHeightMm { get; set; }
            public double RequiredArea { get; set; }
            public double CurrentTotalEffectiveArea { get; set; }
            public double CurrentExactTotalEffectiveArea { get; set; }
            public double ProposedTotalEffectiveArea { get; set; }
            public double ProposedExactTotalEffectiveArea { get; set; }
            public double Deficit { get; set; }
            public string PredictedResult { get; set; }
            public string CurrentResult { get; set; }
            public List<SmokeWindowRetrofitSnapshot> OriginalWindows { get; set; }
            public List<SmokeWindowRetrofitSnapshot> ProposedWindows { get; set; }
        }

        private sealed class StoredSmokeWindowRetrofitOperation
        {
            public string OperationId { get; set; }
            public string DocumentKey { get; set; }
            public IdType RoomId { get; set; }
            public IdType AppliedTypeId { get; set; }
            public IdType CreatedTypeId { get; set; }
            public bool CreatedType { get; set; }
            public double AppliedSillHeightMm { get; set; }
            public double OriginalTotalEffectiveArea { get; set; }
            public double RequiredArea { get; set; }
            public string OriginalResult { get; set; }
            public List<SmokeWindowRetrofitSnapshot> OriginalWindows { get; set; }
        }

        private static readonly object SmokeWindowRetrofitLock = new object();
        private static readonly Dictionary<string, StoredSmokeWindowRetrofitPlan> SmokeWindowRetrofitPlans = new Dictionary<string, StoredSmokeWindowRetrofitPlan>();
        private static readonly Dictionary<string, StoredSmokeWindowRetrofitOperation> SmokeWindowRetrofitOperations = new Dictionary<string, StoredSmokeWindowRetrofitOperation>();
        private const double SmokeRetrofitMmPerFoot = 304.8;

        private object PlanSmokeExhaustWindowRetrofit(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType roomId = parameters["roomId"]?.Value<IdType>() ?? 0;
            JArray targetTokens = parameters["targetWindowIds"] as JArray;
            string requestedType = parameters["targetWindowType"]?.Value<string>();
            double targetSillMm = parameters["targetSillHeightMm"]?.Value<double>() ?? double.NaN;
            double targetWidth = parameters["targetWidthMm"]?.Value<double>() ?? double.NaN;
            double targetHeight = parameters["targetHeightMm"]?.Value<double>() ?? double.NaN;
            if (roomId == 0 || targetTokens == null || targetTokens.Count != 2 || string.IsNullOrWhiteSpace(requestedType))
                throw new Exception("roomId, exactly two targetWindowIds, and targetWindowType are required.");
            if (!IsFinite(targetSillMm) || targetSillMm < 0) throw new Exception("targetSillHeightMm must be a non-negative finite value.");
            if (!IsFinite(targetWidth) || targetWidth <= 0 || !IsFinite(targetHeight) || targetHeight <= 0)
                throw new Exception("targetWidthMm and targetHeightMm must be positive finite values.");
            List<IdType> targetIds = targetTokens.Select(token => token.Value<IdType>()).ToList();
            if (targetIds.Distinct().Count() != 2) throw new Exception("targetWindowIds must be two distinct Window ElementIds.");

            Room room = doc.GetElement(roomId.ToElementId()) as Room;
            if (room == null || room.Area <= 0) throw new Exception($"Room {roomId} was not found or is not placed.");
            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("The Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject { ["levelName"] = level.Name, ["colorize"] = false }));
            JToken checkedRoom = (checker["Rooms"] as JArray)?.FirstOrDefault(item => item["RoomId"]?.Value<IdType>() == roomId);
            if (checkedRoom == null) throw new Exception("The smoke exhaust checker did not return the selected Room.");
            JArray checkerWindows = checkedRoom["Windows"] as JArray ?? new JArray();
            var checkerWindowsById = checkerWindows.Where(item => item["WindowId"] != null).ToDictionary(item => item["WindowId"].Value<IdType>(), item => item);
            foreach (IdType id in targetIds)
                if (!checkerWindowsById.ContainsKey(id)) throw new Exception($"Window {id} does not currently belong to Room {room.Number}.");

            List<SmokeWindowRetrofitSnapshot> originals = new List<SmokeWindowRetrofitSnapshot>();
            foreach (IdType id in targetIds)
            {
                FamilyInstance window = doc.GetElement(id.ToElementId()) as FamilyInstance;
                if (window == null || window.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Windows)
                    throw new Exception($"Target Window {id} no longer exists.");
                Wall host = window.Host as Wall;
                if (host == null) throw new Exception($"Target Window {id} is not hosted by a Revit Wall.");
                JObject checkerWindow = (JObject)checkerWindowsById[id];
                if (checkerWindow["HostWallId"]?.Value<IdType>() != host.Id.GetIdValue())
                    throw new Exception($"Window {id} HostWallId does not match the selected Room checker result.");
                originals.Add(CaptureSmokeWindowRetrofitSnapshot(window, checkerWindow,
                    checkedRoom["SmokeZoneBottom"]?.Value<double>() ?? double.NaN,
                    checkedRoom["SmokeZoneTop"]?.Value<double>() ?? double.NaN));
            }
            if (originals.Select(item => item.HostWallId).Distinct().Count() != 1)
                throw new Exception("The two target Windows must remain on the same existing Host Wall.");
            if (originals.Select(item => item.TypeId).Distinct().Count() != 1)
                throw new Exception("The two target Windows must currently use the same source Type.");
            FamilySymbol sourceSymbol = doc.GetElement(originals[0].TypeId.ToElementId()) as FamilySymbol;
            if (sourceSymbol == null || sourceSymbol.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Windows)
                throw new Exception("The source Window FamilySymbol could not be resolved.");
            string proposedTypeName = requestedType.Contains(":") ? requestedType.Substring(requestedType.IndexOf(':') + 1).Trim() : requestedType.Trim();
            string requestedFamily = requestedType.Contains(":") ? requestedType.Substring(0, requestedType.IndexOf(':')).Trim() : sourceSymbol.FamilyName;
            if (!string.Equals(requestedFamily, sourceSymbol.FamilyName, StringComparison.OrdinalIgnoreCase))
                throw new Exception("Window retrofit target must stay within the source Window Family.");
            FamilySymbol existingTarget = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                .FirstOrDefault(symbol => string.Equals(symbol.FamilyName, sourceSymbol.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(symbol.Name, proposedTypeName, StringComparison.OrdinalIgnoreCase));
            if (existingTarget != null && (Math.Abs(GetSmokeWindowDimensionMm(existingTarget, true) - targetWidth) > 0.5 ||
                Math.Abs(GetSmokeWindowDimensionMm(existingTarget, false) - targetHeight) > 0.5))
                throw new Exception("The existing proposed Window Type has dimensions that do not match the requested TypeSpec.");
            var targetOperation = GetWindowOperationType(sourceSymbol.FamilyName, proposedTypeName);
            if (targetOperation.openingRatio <= 0 || targetOperation.needsConfirm)
                throw new Exception("The proposed Window Type must have a confirmed positive checker OpeningRatio.");

            JObject summary = checkedRoom["Summary"] as JObject ?? new JObject();
            double requiredArea = summary["RequiredArea"]?.Value<double>() ?? Math.Round(room.Area * 0.092903 * 0.02, 4);
            double currentCheckerTotal = summary["TotalEffectiveArea"]?.Value<double>() ?? 0;
            string currentResult = summary["Result"]?.Value<string>() ?? "FAIL";
            double ceilingHeightMm = checkedRoom["SmokeZoneTop"]?.Value<double>() ?? GetCeilingHeight(doc, room, "room_parameter");
            double zoneBottomMm = checkedRoom["SmokeZoneBottom"]?.Value<double>() ?? (ceilingHeightMm - 800.0);
            double zoneTopMm = checkedRoom["SmokeZoneTop"]?.Value<double>() ?? ceilingHeightMm;

            var checkerById = checkerWindowsById;
            double currentExactTotal = 0;
            foreach (JObject checkerWindow in checkerWindows.OfType<JObject>())
            {
                IdType id = checkerWindow["WindowId"]?.Value<IdType>() ?? 0;
                FamilyInstance instance = doc.GetElement(id.ToElementId()) as FamilyInstance;
                if (instance == null) continue;
                SmokeWindowRetrofitSnapshot snapshot = CaptureSmokeWindowRetrofitSnapshot(instance, checkerWindow,
                    checkedRoom["SmokeZoneBottom"]?.Value<double>() ?? double.NaN,
                    checkedRoom["SmokeZoneTop"]?.Value<double>() ?? double.NaN);
                currentExactTotal += snapshot.EffectiveArea;
            }

            double proposedCheckerTotal = currentCheckerTotal - originals.Sum(item => item.CheckerEffectiveArea);
            double proposedExactTotal = currentExactTotal - originals.Sum(item => item.EffectiveArea);
            var proposed = new List<SmokeWindowRetrofitSnapshot>();
            foreach (SmokeWindowRetrofitSnapshot original in originals)
            {
                double proposedHead = targetSillMm + targetHeight;
                double heightInZone = Math.Max(0, Math.Min(proposedHead, zoneTopMm) - Math.Max(targetSillMm, zoneBottomMm));
                double areaInZone = (targetWidth / 1000.0) * (heightInZone / 1000.0);
                double effectiveArea = areaInZone * targetOperation.openingRatio;
                var row = new SmokeWindowRetrofitSnapshot
                {
                    WindowId = original.WindowId, TypeId = existingTarget?.Id.GetIdValue() ?? 0, FamilyName = sourceSymbol.FamilyName,
                    TypeName = proposedTypeName, WidthMm = targetWidth, HeightMm = targetHeight,
                    SillHeightMm = targetSillMm, HeadHeightMm = proposedHead, HostWallId = original.HostWallId,
                    LocationX = original.LocationX, LocationY = original.LocationY,
                    LocationZ = original.LocationZ + targetSillMm - original.SillHeightMm,
                    HeightInZoneMm = heightInZone, AreaInZone = areaInZone, OpeningRatio = targetOperation.openingRatio,
                    EffectiveArea = effectiveArea, CheckerEffectiveArea = Math.Round(effectiveArea, 4)
                };
                proposed.Add(row);
                proposedCheckerTotal += row.CheckerEffectiveArea;
                proposedExactTotal += effectiveArea;
            }

            bool headWithinCeiling = proposed.All(item => item.HeadHeightMm < ceilingHeightMm);
            double effectiveTotal = Math.Round(proposedCheckerTotal, 4);
            string predictedResult = headWithinCeiling && proposedExactTotal > requiredArea && proposedCheckerTotal > requiredArea ? "PASS" : "FAIL";
            string planId = Guid.NewGuid().ToString("N");
            var plan = new StoredSmokeWindowRetrofitPlan
            {
                PlanId = planId, CreatedAtUtc = DateTime.UtcNow, DocumentKey = GetSmokePlanDocumentKey(doc), RoomId = roomId,
                SourceTypeId = sourceSymbol.Id.GetIdValue(), ExistingTargetTypeId = existingTarget?.Id.GetIdValue() ?? 0,
                FamilyName = sourceSymbol.FamilyName, ProposedTypeName = proposedTypeName,
                ProposedWidthMm = targetWidth, ProposedHeightMm = targetHeight,
                TargetSillHeightMm = targetSillMm, CeilingHeightMm = ceilingHeightMm, RequiredArea = requiredArea,
                CurrentTotalEffectiveArea = currentCheckerTotal, CurrentExactTotalEffectiveArea = currentExactTotal,
                ProposedTotalEffectiveArea = effectiveTotal, ProposedExactTotalEffectiveArea = proposedExactTotal,
                Deficit = Math.Max(0, requiredArea - proposedCheckerTotal), PredictedResult = predictedResult,
                CurrentResult = currentResult, OriginalWindows = originals, ProposedWindows = proposed
            };
            lock (SmokeWindowRetrofitLock)
            {
                foreach (string expired in SmokeWindowRetrofitPlans.Values.Where(item => DateTime.UtcNow - item.CreatedAtUtc > TimeSpan.FromMinutes(30)).Select(item => item.PlanId).ToList())
                    SmokeWindowRetrofitPlans.Remove(expired);
                SmokeWindowRetrofitPlans[planId] = plan;
            }

            return new
            {
                Success = true, PlanId = planId, RoomId = roomId, WindowType = plan.FamilyName + ": " + plan.ProposedTypeName,
                ProposedTypeSpec = new { SourceTypeId = plan.SourceTypeId, FamilyName = plan.FamilyName,
                    ProposedTypeName = plan.ProposedTypeName, WidthMm = plan.ProposedWidthMm,
                    HeightMm = plan.ProposedHeightMm, TargetSillHeightMm = plan.TargetSillHeightMm },
                CeilingHeightMm = ceilingHeightMm, SmokeZoneBottom = zoneBottomMm, SmokeZoneTop = zoneTopMm,
                OriginalWindows = originals, ProposedWindows = proposed,
                CurrentTotalEffectiveArea = currentCheckerTotal, CurrentExactTotalEffectiveArea = currentExactTotal,
                ProposedTotalEffectiveArea = effectiveTotal, ProposedExactTotalEffectiveArea = proposedExactTotal,
                RequiredArea = requiredArea, Deficit = plan.Deficit, PredictedResult = predictedResult,
                HeadWithinCeiling = headWithinCeiling, CanApply = predictedResult == "PASS"
            };
        }

        private object ApplySmokeExhaustWindowRetrofit(JObject parameters)
        {
            string planId = parameters["planId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(planId)) throw new Exception("planId is required.");
            StoredSmokeWindowRetrofitPlan plan;
            lock (SmokeWindowRetrofitLock)
                if (!SmokeWindowRetrofitPlans.TryGetValue(planId, out plan)) throw new Exception("The Window retrofit plan was not found or has already been applied.");
            if (DateTime.UtcNow - plan.CreatedAtUtc > TimeSpan.FromMinutes(30)) throw new Exception("The Window retrofit plan expired; create a fresh preview.");
            if (plan.PredictedResult != "PASS" || plan.ProposedExactTotalEffectiveArea <= plan.RequiredArea || plan.ProposedWindows.Any(item => item.HeadHeightMm >= plan.CeilingHeightMm))
                throw new Exception("The retrofit plan cannot be applied: it must pass the exact area and ceiling-height checks first.");

            Document doc = _uiApp.ActiveUIDocument.Document;
            if (plan.DocumentKey != GetSmokePlanDocumentKey(doc)) throw new Exception("The plan belongs to a different Revit document.");
            FamilySymbol sourceSymbol = doc.GetElement(plan.SourceTypeId.ToElementId()) as FamilySymbol;
            if (sourceSymbol == null || sourceSymbol.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Windows ||
                !string.Equals(sourceSymbol.FamilyName, plan.FamilyName, StringComparison.OrdinalIgnoreCase))
                throw new Exception("The source Window Type no longer exists or its Family changed.");
            Room room = doc.GetElement(plan.RoomId.ToElementId()) as Room;
            if (room == null) throw new Exception("The target Room no longer exists.");

            var targets = ValidateSmokeWindowRetrofitState(doc, room, plan.OriginalWindows, requireOriginalTypeAndSill: true, 0, plan.TargetSillHeightMm);
            var applied = new List<SmokeWindowRetrofitSnapshot>();
            FamilySymbol targetSymbol = null;
            bool createdType = false;
            using (Transaction trans = new Transaction(doc, "Apply Smoke Exhaust Window Retrofit"))
            {
                trans.Start();
                try
                {
                    targetSymbol = plan.ExistingTargetTypeId != 0
                        ? doc.GetElement(plan.ExistingTargetTypeId.ToElementId()) as FamilySymbol
                        : null;
                    if (targetSymbol == null)
                    {
                        targetSymbol = new FilteredElementCollector(doc).OfClass(typeof(FamilySymbol)).Cast<FamilySymbol>()
                            .FirstOrDefault(symbol => string.Equals(symbol.FamilyName, plan.FamilyName, StringComparison.OrdinalIgnoreCase) &&
                                string.Equals(symbol.Name, plan.ProposedTypeName, StringComparison.OrdinalIgnoreCase));
                    }
                    if (targetSymbol == null)
                    {
                        FamilySymbol duplicate = sourceSymbol.Duplicate(plan.ProposedTypeName) as FamilySymbol;
                        if (duplicate == null) throw new Exception("Revit did not create a duplicate Window FamilySymbol.");
                        createdType = true;
                        targetSymbol = duplicate;
                        SetSmokeWindowTypeDimension(targetSymbol, new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH },
                            new[] { "Width" }, plan.ProposedWidthMm, "Width");
                        SetSmokeWindowTypeDimension(targetSymbol, new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT },
                            new[] { "Height" }, plan.ProposedHeightMm, "Height");
                        doc.Regenerate();
                    }
                    if (!string.Equals(targetSymbol.FamilyName, plan.FamilyName, StringComparison.OrdinalIgnoreCase))
                        throw new Exception("The resolved retrofit Type is not in the source Window Family.");
                    if (Math.Abs(GetSmokeWindowDimensionMm(targetSymbol, true) - plan.ProposedWidthMm) > 0.5 ||
                        Math.Abs(GetSmokeWindowDimensionMm(targetSymbol, false) - plan.ProposedHeightMm) > 0.5)
                        throw new Exception("The retrofit Type dimensions do not match the planned Width/Height.");

                    foreach (FamilyInstance window in targets)
                    {
                        SmokeWindowRetrofitSnapshot original = plan.OriginalWindows.First(item => item.WindowId == window.Id.GetIdValue());
                        window.Symbol = targetSymbol;
                        doc.Regenerate();
                        ValidateSmokeWindowPreservedInstanceState(window, original);
                        Parameter sill = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sill == null || sill.IsReadOnly || !sill.Set(plan.TargetSillHeightMm / SmokeRetrofitMmPerFoot))
                            throw new Exception($"Window {original.WindowId} Sill Height could not be set.");
                        doc.Regenerate();
                        ValidateSmokeWindowPreservedInstanceState(window, original);
                        ValidateSmokeWindowHostAndXY(window, original);
                        double actualHead = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM }, new[] { "Head Height" }) ?? 0) * SmokeRetrofitMmPerFoot;
                        if (actualHead >= plan.CeilingHeightMm) throw new Exception($"Window {original.WindowId} HeadHeight must remain below the Room ceiling.");
                        SmokeWindowRetrofitSnapshot proposedRow = plan.ProposedWindows.First(item => item.WindowId == original.WindowId);
                        if (Math.Abs(actualHead - proposedRow.HeadHeightMm) > 0.5) throw new Exception($"Window {original.WindowId} actual Head Height does not match the simulated result.");
                    }

                    EnsureSmokeRetrofitCheckerResult(doc, room, mustPass: true);
                    applied.AddRange(plan.ProposedWindows);
                    trans.Commit();
                }
                catch
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    throw;
                }
            }

            string operationId = Guid.NewGuid().ToString("N");
            var operation = new StoredSmokeWindowRetrofitOperation
            {
                OperationId = operationId, DocumentKey = plan.DocumentKey, RoomId = plan.RoomId,
                AppliedTypeId = targetSymbol.Id.GetIdValue(), CreatedTypeId = createdType ? targetSymbol.Id.GetIdValue() : 0,
                CreatedType = createdType, AppliedSillHeightMm = plan.TargetSillHeightMm,
                OriginalTotalEffectiveArea = plan.CurrentTotalEffectiveArea, OriginalResult = plan.CurrentResult,
                RequiredArea = plan.RequiredArea,
                OriginalWindows = plan.OriginalWindows
            };
            lock (SmokeWindowRetrofitLock)
            {
                SmokeWindowRetrofitOperations[operationId] = operation;
                SmokeWindowRetrofitPlans.Remove(plan.PlanId);
            }
            return new { Success = true, OperationId = operationId, RoomId = plan.RoomId,
                ModifiedElementIds = applied.Select(item => item.WindowId).ToList(), OriginalState = plan.OriginalWindows,
                AppliedState = applied, CreatedType = createdType, CreatedTypeId = createdType ? targetSymbol.Id.GetIdValue() : (IdType?)null,
                Recheck = new { TotalEffectiveArea = plan.ProposedTotalEffectiveArea, RequiredArea = plan.RequiredArea, Result = "PASS" } };
        }

        private object UndoSmokeExhaustWindowRetrofit(JObject parameters)
        {
            string operationId = parameters["operationId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(operationId)) throw new Exception("operationId is required.");
            StoredSmokeWindowRetrofitOperation operation;
            lock (SmokeWindowRetrofitLock)
                if (!SmokeWindowRetrofitOperations.TryGetValue(operationId, out operation)) throw new Exception("The Window retrofit operation was not found or has already been undone.");
            Document doc = _uiApp.ActiveUIDocument.Document;
            if (operation.DocumentKey != GetSmokePlanDocumentKey(doc)) throw new Exception("The operation belongs to a different Revit document.");
            Room room = doc.GetElement(operation.RoomId.ToElementId()) as Room;
            if (room == null) throw new Exception("The original Room no longer exists.");
            var windows = ValidateSmokeWindowRetrofitState(doc, room, operation.OriginalWindows, requireOriginalTypeAndSill: false, operation.AppliedTypeId, operation.AppliedSillHeightMm);
            using (Transaction trans = new Transaction(doc, "Undo Smoke Exhaust Window Retrofit"))
            {
                trans.Start();
                try
                {
                    foreach (FamilyInstance window in windows)
                    {
                        SmokeWindowRetrofitSnapshot original = operation.OriginalWindows.First(item => item.WindowId == window.Id.GetIdValue());
                        FamilySymbol originalSymbol = doc.GetElement(original.TypeId.ToElementId()) as FamilySymbol;
                        if (originalSymbol == null) throw new Exception($"Original Window Type {original.TypeId} no longer exists.");
                        window.Symbol = originalSymbol;
                        doc.Regenerate();
                        Parameter sill = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sill == null || sill.IsReadOnly || !sill.Set(original.SillHeightMm / SmokeRetrofitMmPerFoot))
                            throw new Exception($"Window {original.WindowId} original Sill Height could not be restored.");
                        doc.Regenerate();
                        ValidateSmokeWindowPreservedInstanceState(window, original);
                        ValidateSmokeWindowHostAndXY(window, original);
                        double restoredHead = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM }, new[] { "Head Height" }) ?? 0) * SmokeRetrofitMmPerFoot;
                        if (Math.Abs(restoredHead - original.HeadHeightMm) > 0.5) throw new Exception($"Window {original.WindowId} original Head Height could not be restored.");
                        ValidateSmokeWindowExactLocation(window, original);
                    }
                    EnsureSmokeRetrofitCheckerResult(doc, room, mustPass: operation.OriginalResult == "PASS");
                    bool removedCreatedType = false;
                    bool retainedCreatedType = false;
                    if (operation.CreatedType && operation.CreatedTypeId != 0)
                    {
                        FamilySymbol created = doc.GetElement(operation.CreatedTypeId.ToElementId()) as FamilySymbol;
                        if (created != null)
                        {
                            bool inUse = new FilteredElementCollector(doc).OfClass(typeof(FamilyInstance)).Cast<FamilyInstance>()
                                .Any(instance => instance.Symbol?.Id.GetIdValue() == operation.CreatedTypeId) ||
                                CaptureSmokeGlobalParameterAssociations(created).Count > 0;
                            if (inUse) retainedCreatedType = true;
                            else { doc.Delete(created.Id); removedCreatedType = true; }
                        }
                    }
                    trans.Commit();
                    lock (SmokeWindowRetrofitLock) SmokeWindowRetrofitOperations.Remove(operationId);
                    return new { Success = true, OperationId = operationId,
                        RestoredElementIds = operation.OriginalWindows.Select(item => item.WindowId).ToList(),
                        RestoredState = operation.OriginalWindows,
                        CreatedTypeDeleted = removedCreatedType, CreatedTypeRetainedInUse = retainedCreatedType,
                        Recheck = new { TotalEffectiveArea = operation.OriginalTotalEffectiveArea, Result = operation.OriginalResult } };
                }
                catch
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    throw;
                }
            }
            throw new InvalidOperationException("Unreachable undo flow.");
        }

        private SmokeWindowRetrofitSnapshot CaptureSmokeWindowRetrofitSnapshot(FamilyInstance window, JObject checkerRow, double zoneBottomMm, double zoneTopMm)
        {
            const double mmPerFoot = 304.8;
            var operation = GetWindowOperationType(window.Symbol.FamilyName, window.Symbol.Name);
            double width = GetSmokeWindowInstanceDimensionMm(window, true);
            double height = GetSmokeWindowInstanceDimensionMm(window, false);
            double sill = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM }, new[] { "Sill Height" }) ?? 0) * mmPerFoot;
            double head = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM }, new[] { "Head Height" }) ?? (sill / mmPerFoot + height / mmPerFoot)) * mmPerFoot;
            LocationPoint point = window.Location as LocationPoint;
            if (point == null) throw new Exception($"Window {window.Id.GetIdValue()} has no LocationPoint.");
            IdType hostId = window.Host?.Id.GetIdValue() ?? 0;
            if (hostId == 0) throw new Exception($"Window {window.Id.GetIdValue()} has no Host element.");
            XYZ facing = window.FacingOrientation, hand = window.HandOrientation;
            Parameter inset = window.LookupParameter("Window Inset");
            double heightInZone = Math.Max(0, Math.Min(head, zoneTopMm) - Math.Max(sill, zoneBottomMm));
            double areaInZone = width / 1000.0 * (heightInZone / 1000.0);
            double effective = areaInZone * operation.openingRatio;
            double checkerEffective = checkerRow?["EffectiveArea"]?.Value<double>() ?? Math.Round(effective, 4);
            return new SmokeWindowRetrofitSnapshot
            {
                WindowId = window.Id.GetIdValue(), TypeId = window.Symbol.Id.GetIdValue(), FamilyName = window.Symbol.FamilyName,
                TypeName = window.Symbol.Name, WidthMm = width, HeightMm = height, SillHeightMm = sill,
                HeadHeightMm = head, HostWallId = hostId, LocationX = point.Point.X * mmPerFoot,
                LocationY = point.Point.Y * mmPerFoot, LocationZ = point.Point.Z * mmPerFoot,
                FacingX = facing.X, FacingY = facing.Y, FacingZ = facing.Z,
                HandX = hand.X, HandY = hand.Y, HandZ = hand.Z,
                WindowInsetInternalValue = inset != null && inset.StorageType == StorageType.Double ? (double?)inset.AsDouble() : null,
                GlobalParameterAssociations = CaptureSmokeGlobalParameterAssociations(window),
                InstanceParameterValues = CaptureSmokeInstanceParameterValues(window),
                HeightInZoneMm = heightInZone, AreaInZone = areaInZone, OpeningRatio = operation.openingRatio,
                EffectiveArea = effective, CheckerEffectiveArea = checkerEffective
            };
        }

        private void SetSmokeWindowTypeDimension(FamilySymbol symbol, BuiltInParameter[] builtIns, string[] names,
            double valueMm, string label)
        {
            Parameter parameter = null;
            foreach (BuiltInParameter builtIn in builtIns)
            {
                try { parameter = symbol.get_Parameter(builtIn); } catch { }
                if (parameter != null) break;
            }
            if (parameter == null)
                foreach (string name in names)
                {
                    parameter = symbol.LookupParameter(name);
                    if (parameter != null) break;
                }
            if (parameter == null) throw new Exception($"Duplicated Window Type is missing {label}.");
            if (parameter.StorageType != StorageType.Double) throw new Exception($"Duplicated Window Type {label} must use StorageType.Double.");
            if (parameter.IsReadOnly) throw new Exception($"Duplicated Window Type {label} is read-only.");
            if (!parameter.Set(valueMm / SmokeRetrofitMmPerFoot)) throw new Exception($"Duplicated Window Type {label} could not be set.");
        }

        private Dictionary<IdType, IdType> CaptureSmokeGlobalParameterAssociations(Element element)
        {
            var associations = new Dictionary<IdType, IdType>();
            foreach (Parameter parameter in element.Parameters)
            {
                if (parameter == null) continue;
                ElementId globalId;
                try { globalId = parameter.GetAssociatedGlobalParameter(); }
                catch { continue; }
                if (globalId == null || globalId == ElementId.InvalidElementId) continue;
                associations[parameter.Id.GetIdValue()] = globalId.GetIdValue();
            }
            return associations;
        }

        private Dictionary<IdType, SmokeWindowInstanceParameterValue> CaptureSmokeInstanceParameterValues(Element element)
        {
            var values = new Dictionary<IdType, SmokeWindowInstanceParameterValue>();
            string[] retrofitDependentNames = { "Sill Height", "Head Height", "Width", "Height", "Type", "Family", "Family and Type", "Type Id" };
            foreach (Parameter parameter in element.Parameters)
            {
                string name = parameter?.Definition?.Name;
                if (parameter == null || string.IsNullOrEmpty(name) ||
                    retrofitDependentNames.Contains(name, StringComparer.OrdinalIgnoreCase) ||
                    IsSmokeWindowReadOnlyGeometryDerivedParameter(parameter)) continue;
                string value;
                switch (parameter.StorageType)
                {
                    case StorageType.Double: value = parameter.AsDouble().ToString("R", System.Globalization.CultureInfo.InvariantCulture); break;
                    case StorageType.Integer: value = parameter.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture); break;
                    case StorageType.String: value = parameter.AsString() ?? string.Empty; break;
                    case StorageType.ElementId: value = parameter.AsElementId()?.GetIdValue().ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty; break;
                    default: value = string.Empty; break;
                }
                values[parameter.Id.GetIdValue()] = new SmokeWindowInstanceParameterValue
                {
                    Name = name,
                    Value = parameter.StorageType + ":" + value
                };
            }
            return values;
        }

        private bool IsSmokeWindowReadOnlyGeometryDerivedParameter(Parameter parameter)
        {
            if (parameter == null || !parameter.IsReadOnly || parameter.StorageType != StorageType.Double) return false;
            ForgeTypeId dataType;
            try { dataType = parameter.Definition?.GetDataType(); }
            catch { return false; }
            if (dataType == null) return false;

            // Read-only measurable values (length/area/volume) are calculated from the
            // instance geometry or its assigned Type. Exclude them from generic equality
            // protection; writable values and other read-only parameters remain protected.
            string typeId;
            try { typeId = dataType.TypeId; }
            catch { return false; }
            return typeId == SpecTypeId.Length.TypeId ||
                typeId == SpecTypeId.Area.TypeId ||
                typeId == SpecTypeId.Volume.TypeId;
        }

        private void ValidateSmokeWindowPreservedInstanceState(FamilyInstance window, SmokeWindowRetrofitSnapshot original)
        {
            Dictionary<IdType, IdType> associations = CaptureSmokeGlobalParameterAssociations(window);
            if (original.GlobalParameterAssociations == null || associations.Count != original.GlobalParameterAssociations.Count ||
                original.GlobalParameterAssociations.Any(pair => !associations.TryGetValue(pair.Key, out IdType value) || value != pair.Value))
                throw new Exception($"Window {original.WindowId} Global Parameter associations changed during same-Family Type change.");
            Dictionary<IdType, SmokeWindowInstanceParameterValue> values = CaptureSmokeInstanceParameterValues(window);
            var differences = new List<string>();
            if (original.InstanceParameterValues != null)
            {
                foreach (KeyValuePair<IdType, SmokeWindowInstanceParameterValue> pair in original.InstanceParameterValues)
                {
                    if (!values.TryGetValue(pair.Key, out SmokeWindowInstanceParameterValue after))
                        differences.Add($"{pair.Value.Name}: before={pair.Value.Value}, after=<missing>");
                    else if (after.Value != pair.Value.Value)
                        differences.Add($"{pair.Value.Name}: before={pair.Value.Value}, after={after.Value}");
                }
            }
            foreach (KeyValuePair<IdType, SmokeWindowInstanceParameterValue> pair in values)
                if (original.InstanceParameterValues == null || !original.InstanceParameterValues.ContainsKey(pair.Key))
                    differences.Add($"{pair.Value.Name}: before=<missing>, after={pair.Value.Value}");
            if (differences.Count > 0)
                throw new Exception($"Window {original.WindowId} protected Instance Parameters changed during retrofit: {string.Join("; ", differences)}");
            Parameter inset = window.LookupParameter("Window Inset");
            if (original.WindowInsetInternalValue.HasValue && (inset == null || inset.StorageType != StorageType.Double ||
                Math.Abs(inset.AsDouble() - original.WindowInsetInternalValue.Value) > 1e-9))
                throw new Exception($"Window {original.WindowId} Window Inset changed during retrofit.");
            XYZ facing = window.FacingOrientation, hand = window.HandOrientation;
            if (facing.DotProduct(new XYZ(original.FacingX, original.FacingY, original.FacingZ)) < 0.999999 ||
                hand.DotProduct(new XYZ(original.HandX, original.HandY, original.HandZ)) < 0.999999)
                throw new Exception($"Window {original.WindowId} Facing/Hand orientation changed during retrofit.");
        }

        private double GetSmokeWindowInstanceDimensionMm(FamilyInstance window, bool width)
        {
            BuiltInParameter[] bips = width
                ? new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH }
                : new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT };
            string[] names = width ? new[] { "Width", "撖砍漲" } : new[] { "Height", "擃漲" };
            double? value = GetParamValue(window, bips, names);
            if (!value.HasValue || value.Value == 0) value = GetParamValue(window.Symbol, bips, names);
            return (value ?? 0) * SmokeRetrofitMmPerFoot;
        }

        private List<FamilyInstance> ValidateSmokeWindowRetrofitState(Document doc, Room room, List<SmokeWindowRetrofitSnapshot> snapshots,
            bool requireOriginalTypeAndSill, IdType expectedTypeId, double expectedSillMm)
        {
            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("The Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject { ["levelName"] = level.Name, ["colorize"] = false }));
            JToken checkedRoom = (checker["Rooms"] as JArray)?.FirstOrDefault(item => item["RoomId"]?.Value<IdType>() == room.Id.GetIdValue());
            var roomWindowIds = new HashSet<IdType>((checkedRoom?["Windows"] as JArray ?? new JArray()).Where(item => item["WindowId"] != null).Select(item => item["WindowId"].Value<IdType>()));
            var result = new List<FamilyInstance>();
            foreach (SmokeWindowRetrofitSnapshot snapshot in snapshots)
            {
                FamilyInstance window = doc.GetElement(snapshot.WindowId.ToElementId()) as FamilyInstance;
                if (window == null || window.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Windows)
                    throw new Exception($"Target Window {snapshot.WindowId} no longer exists.");
                if (!roomWindowIds.Contains(snapshot.WindowId)) throw new Exception($"Window {snapshot.WindowId} no longer belongs to Room {room.Number}.");
                if (window.Host?.Id.GetIdValue() != snapshot.HostWallId) throw new Exception($"Window {snapshot.WindowId} HostWallId changed; operation stopped.");
                ValidateSmokeWindowHostAndXY(window, snapshot);
                IdType mustHaveType = requireOriginalTypeAndSill ? snapshot.TypeId : expectedTypeId;
                double mustHaveSill = requireOriginalTypeAndSill ? snapshot.SillHeightMm : expectedSillMm;
                double actualSill = (GetParamValue(window, new[] { BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM }, new[] { "Sill Height" }) ?? 0) * SmokeRetrofitMmPerFoot;
                if (window.Symbol.Id.GetIdValue() != mustHaveType || Math.Abs(actualSill - mustHaveSill) > 0.5)
                    throw new Exception($"Window {snapshot.WindowId} Type or Sill Height no longer matches the expected state.");
                result.Add(window);
            }
            return result;
        }

        private void ValidateSmokeWindowHostAndXY(FamilyInstance window, SmokeWindowRetrofitSnapshot snapshot)
        {
            if (window.Host?.Id.GetIdValue() != snapshot.HostWallId) throw new Exception($"Window {snapshot.WindowId} HostWallId changed.");
            LocationPoint point = window.Location as LocationPoint;
            if (point == null) throw new Exception($"Window {snapshot.WindowId} has no LocationPoint.");
            double x = point.Point.X * SmokeRetrofitMmPerFoot, y = point.Point.Y * SmokeRetrofitMmPerFoot;
            if (Math.Abs(x - snapshot.LocationX) > 1.0 || Math.Abs(y - snapshot.LocationY) > 1.0)
                throw new Exception($"Window {snapshot.WindowId} moved in XY; operation stopped.");
        }

        private void ValidateSmokeWindowExactLocation(FamilyInstance window, SmokeWindowRetrofitSnapshot snapshot)
        {
            LocationPoint point = window.Location as LocationPoint;
            if (point == null) throw new Exception($"Window {snapshot.WindowId} has no LocationPoint.");
            if (Math.Abs(point.Point.X * SmokeRetrofitMmPerFoot - snapshot.LocationX) > 1.0 ||
                Math.Abs(point.Point.Y * SmokeRetrofitMmPerFoot - snapshot.LocationY) > 1.0 ||
                Math.Abs(point.Point.Z * SmokeRetrofitMmPerFoot - snapshot.LocationZ) > 1.0)
                throw new Exception($"Window {snapshot.WindowId} original location could not be restored.");
        }

        private void EnsureSmokeRetrofitCheckerResult(Document doc, Room room, bool mustPass)
        {
            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("Checker revalidation failed: Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject { ["levelName"] = level.Name, ["colorize"] = false }));
            JToken checkedRoom = (checker["Rooms"] as JArray)?.FirstOrDefault(item => item["RoomId"]?.Value<IdType>() == room.Id.GetIdValue());
            JObject summary = checkedRoom?["Summary"] as JObject;
            if (summary == null || summary["TotalEffectiveArea"] == null || summary["RequiredArea"] == null || summary["Result"] == null)
                throw new Exception($"Checker revalidation failed: Room {room.Number} result or Summary fields are missing.");

            // Revalidate against the actual Room-specific checker Summary. Do not compare
            // against a plan-time predicted area: the checker rounds its displayed areas.
            double total = summary["TotalEffectiveArea"].Value<double>();
            double requiredArea = summary["RequiredArea"].Value<double>();
            string result = summary["Result"].Value<string>();
            bool valid = mustPass
                ? result == "PASS" && total >= requiredArea
                : result == "FAIL";
            if (!valid)
                throw new Exception($"Checker revalidation failed: TotalEffectiveArea={total}, RequiredArea={requiredArea}, Result={result}.");
        }
    }
}
