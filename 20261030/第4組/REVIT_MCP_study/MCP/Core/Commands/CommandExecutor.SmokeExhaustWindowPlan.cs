using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using Autodesk.Revit.UI;
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
        private const double SmokePlanMmPerFoot = 304.8;
        private const double SmokePlanEndClearanceMm = 300.0;
        private static readonly object SmokePlanStateLock = new object();
        private static readonly Dictionary<string, StoredSmokeWindowPlan> SmokeWindowPlans = new Dictionary<string, StoredSmokeWindowPlan>();
        private static readonly Dictionary<string, StoredSmokeWindowOperation> SmokeWindowOperations = new Dictionary<string, StoredSmokeWindowOperation>();

        private sealed class SmokeWindowPlacement
        {
            public IdType HostWallId { get; set; }
            public string ExteriorValidationReason { get; set; }
            public double LocationX { get; set; }
            public double LocationY { get; set; }
            public double SillHeightMm { get; set; }
            public double WidthMm { get; set; }
            public double HeightMm { get; set; }
            public List<string> ConflictReasons { get; set; }
        }

        private sealed class StoredSmokeWindowPlan
        {
            public string PlanId { get; set; }
            public DateTime CreatedAtUtc { get; set; }
            public string DocumentKey { get; set; }
            public IdType RoomId { get; set; }
            public string WindowType { get; set; }
            public IdType TypeId { get; set; }
            public int Count { get; set; }
            public double RequiredArea { get; set; }
            public double CurrentEffectiveArea { get; set; }
            public double AddedEffectiveArea { get; set; }
            public double PredictedTotalEffectiveArea { get; set; }
            public double PredictedDeficit { get; set; }
            public string PredictedResult { get; set; }
            public List<SmokeWindowPlacement> Placements { get; set; }
        }

        private sealed class StoredSmokeWindowOperation
        {
            public string OperationId { get; set; }
            public string DocumentKey { get; set; }
            public IdType RoomId { get; set; }
            public string WindowType { get; set; }
            public List<IdType> CreatedElementIds { get; set; }
            public List<SmokeWindowPlacement> Placements { get; set; }
        }

        private sealed class SmokeWindowDeleteSnapshot
        {
            public IdType ElementId { get; set; }
            public IdType CategoryId { get; set; }
            public IdType TypeId { get; set; }
            public IdType HostWallId { get; set; }
        }

        private sealed class OuterWallSegment
        {
            public IdType WallId { get; set; }
            public XYZ Start { get; set; }
            public XYZ End { get; set; }
            public double LengthMm { get; set; }
            public bool IsExterior { get; set; }
            public bool HasExistingRoomWindow { get; set; }
            public List<string> AdjacentRooms { get; set; } = new List<string>();
            public List<string> IgnoredOtherRooms { get; set; } = new List<string>();
            public string ExteriorValidationReason { get; set; }
            public bool IsValidExterior { get; set; }
            public int ExteriorConfidenceRank { get; set; }
            public string ExteriorConfidence { get; set; }
        }

        private sealed class RoomBoundaryReference
        {
            public string Label { get; set; }
            public bool SameLevelAndVerticalOverlap { get; set; }
            public XYZ Start { get; set; }
            public XYZ End { get; set; }
        }

        private object PlanSmokeExhaustWindowPlan(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            IdType roomId = parameters["roomId"]?.Value<IdType>() ?? 0;
            string requestedType = parameters["windowType"]?.Value<string>();
            int count = parameters["count"]?.Value<int>() ?? 0;
            double sillHeightMm = parameters["sillHeightMm"]?.Value<double>() ?? double.NaN;
            double? requestedWidthMm = parameters["widthMm"]?.Value<double?>();
            double? requestedHeightMm = parameters["heightMm"]?.Value<double?>();
            if (roomId == 0 || string.IsNullOrWhiteSpace(requestedType)) throw new Exception("roomId and windowType are required.");
            if (count < 1) throw new Exception("count must be at least 1.");
            if (!IsFinite(sillHeightMm) || sillHeightMm < 0) throw new Exception("sillHeightMm must be a non-negative number.");

            Room room = doc.GetElement(roomId.ToElementId()) as Room;
            if (room == null || room.Area <= 0) throw new Exception($"Room {roomId} was not found or is not placed.");
            FamilySymbol symbol = ResolveSmokeWindowSymbol(doc, requestedType);
            double widthMm = GetSmokeWindowDimensionMm(symbol, true);
            double heightMm = GetSmokeWindowDimensionMm(symbol, false);
            if (widthMm <= 0 || heightMm <= 0) throw new Exception($"Window type '{requestedType}' has no readable positive Width/Height.");
            if (requestedWidthMm.HasValue && Math.Abs(requestedWidthMm.Value - widthMm) > 2.0)
                throw new Exception("Candidate width does not match the selected Revit Window Type.");
            if (requestedHeightMm.HasValue && Math.Abs(requestedHeightMm.Value - heightMm) > 2.0)
                throw new Exception("Candidate height does not match the selected Revit Window Type.");

            List<OuterWallSegment> outerSegments = GetOuterWallSegments(doc, room);
            if (outerSegments.Count == 0) throw new Exception("The Room outer boundary has no straight hosted Revit Walls suitable for Window placement.");

            List<OuterWallSegment> validExteriorSegments = outerSegments.Where(segment => segment.IsValidExterior).ToList();
            List<SmokeWindowPlacement> placements = FindSmokeWindowPlacements(doc, validExteriorSegments, widthMm, heightMm, sillHeightMm, count);
            while (placements.Count > 0 && GetSmokeWindowPlacementValidationError(doc, validExteriorSegments, placements) != null)
                placements.RemoveAt(placements.Count - 1);
            var candidateBoundaryWalls = outerSegments.GroupBy(segment => segment.WallId).Select(group =>
            {
                OuterWallSegment segment = group.First();
                int capacity = segment.IsValidExterior
                    ? FindSmokeWindowPlacements(doc, new List<OuterWallSegment> { segment }, widthMm, heightMm, sillHeightMm, count).Count
                    : 0;
                return new { WallId = segment.WallId, LengthMm = Math.Round(segment.LengthMm, 2), IsExterior = segment.IsValidExterior,
                    IsValidExteriorWall = segment.IsValidExterior, WallTypeFunction = segment.IsExterior ? "Exterior" : "Other",
                    segment.ExteriorConfidence, segment.ExteriorValidationReason, AdjacentRooms = segment.AdjacentRooms,
                    IgnoredOtherRooms = segment.IgnoredOtherRooms, HasExistingRoomWindow = segment.HasExistingRoomWindow,
                    PlacementCapacity = capacity };
            }).ToList();
            if (placements.Count != count)
            {
                List<SmokeWindowPlacement> diagnosticPlacements = FindSmokeWindowDiagnosticPlacements(
                    doc, outerSegments, placements, widthMm, heightMm, sillHeightMm, count - placements.Count);
                Level diagnosticLevel = doc.GetElement(room.LevelId) as Level;
                return new { Success = false, PlacementSufficient = false, RequestedCount = count, FoundPositions = placements.Count,
                    Reason = $"Only {placements.Count} of {count} placements fit on valid exterior walls; adjacent Room walls are excluded.",
                    CanApply = false, CandidateBoundaryWalls = candidateBoundaryWalls, Placements = placements,
                    DiagnosticPlacements = diagnosticPlacements.Select(placement => new
                    {
                        placement.HostWallId,
                        X = placement.LocationX,
                        Y = placement.LocationY,
                        Z = (diagnosticLevel?.Elevation ?? 0) * SmokePlanMmPerFoot + placement.SillHeightMm,
                        placement.LocationX,
                        placement.LocationY,
                        Width = placement.WidthMm,
                        Height = placement.HeightMm,
                        SillHeight = placement.SillHeightMm,
                        HeadHeight = placement.SillHeightMm + placement.HeightMm,
                        placement.WidthMm,
                        placement.HeightMm,
                        placement.SillHeightMm,
                        HeadHeightMm = placement.SillHeightMm + placement.HeightMm,
                        IsExecutable = false,
                        ConflictReasons = placement.ConflictReasons?.ToArray()
                            ?? new[] { "未能通過正式配置搜尋，僅供診斷預覽" }
                    }).ToList()
                };
            }

            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("The Room level could not be resolved.");
            JObject checker = JObject.FromObject(CheckSmokeExhaustWindows(new JObject
            {
                ["levelName"] = level.Name,
                ["colorize"] = false
            }));
            JToken checkedRoom = (checker["Rooms"] as JArray)?.FirstOrDefault(item => item["RoomId"]?.Value<IdType>() == roomId);
            if (checkedRoom == null) throw new Exception("The existing smoke exhaust checker did not return the selected Room.");
            JObject summary = checkedRoom["Summary"] as JObject ?? new JObject();
            double currentArea = summary["TotalEffectiveArea"]?.Value<double>() ?? 0;
            double requiredArea = summary["RequiredArea"]?.Value<double>() ?? Math.Round(room.Area * 0.092903 * 0.02, 4);
            double ceilingHeightMm = GetCeilingHeight(doc, room, "room_parameter");
            double zoneBottomMm = checkedRoom["SmokeZoneBottom"]?.Value<double>() ?? (ceilingHeightMm - 800.0);
            double zoneTopMm = checkedRoom["SmokeZoneTop"]?.Value<double>() ?? ceilingHeightMm;
            if (Math.Min(sillHeightMm + heightMm, zoneTopMm) <= Math.Max(sillHeightMm, zoneBottomMm))
                throw new Exception("The requested Window would not intersect the effective smoke zone.");
            var operation = GetWindowOperationType(symbol.FamilyName, symbol.Name);
            if (operation.openingRatio <= 0 || operation.needsConfirm)
                throw new Exception("The selected Window Type does not have a confirmed positive smoke opening ratio.");
            double effectiveHeightMm = Math.Max(0, Math.Min(sillHeightMm + heightMm, zoneTopMm) - Math.Max(sillHeightMm, zoneBottomMm));
            double addedArea = Math.Round(count * widthMm / 1000.0 * effectiveHeightMm / 1000.0 * operation.openingRatio, 4);
            double predictedTotal = Math.Round(currentArea + addedArea, 4);
            double predictedDeficit = Math.Round(Math.Max(requiredArea - predictedTotal, 0), 4);
            string planId = Guid.NewGuid().ToString("N");

            var stored = new StoredSmokeWindowPlan
            {
                PlanId = planId,
                CreatedAtUtc = DateTime.UtcNow,
                DocumentKey = GetSmokePlanDocumentKey(doc),
                RoomId = roomId,
                WindowType = symbol.FamilyName + ": " + symbol.Name,
                TypeId = symbol.Id.GetIdValue(),
                Count = count,
                RequiredArea = requiredArea,
                CurrentEffectiveArea = currentArea,
                AddedEffectiveArea = addedArea,
                PredictedTotalEffectiveArea = predictedTotal,
                PredictedDeficit = predictedDeficit,
                PredictedResult = predictedTotal >= requiredArea ? "PASS" : "FAIL",
                Placements = placements
            };
            lock (SmokePlanStateLock)
            {
                foreach (string expiredId in SmokeWindowPlans.Values.Where(item => DateTime.UtcNow - item.CreatedAtUtc > TimeSpan.FromMinutes(30)).Select(item => item.PlanId).ToList())
                    SmokeWindowPlans.Remove(expiredId);
                while (SmokeWindowPlans.Count >= 50)
                {
                    string oldestId = SmokeWindowPlans.Values.OrderBy(item => item.CreatedAtUtc).First().PlanId;
                    SmokeWindowPlans.Remove(oldestId);
                }
                SmokeWindowPlans[planId] = stored;
            }

            return new
            {
                PlanId = planId,
                RoomId = roomId,
                WindowType = stored.WindowType,
                Count = count,
                PredictedResult = new
                {
                    CurrentEffectiveArea = currentArea,
                    AddedEffectiveArea = addedArea,
                    TotalEffectiveArea = predictedTotal,
                    RequiredArea = requiredArea,
                    Deficit = predictedDeficit,
                    Result = stored.PredictedResult
                },
                PlacementSufficient = true,
                FoundPositions = placements.Count,
                CandidateBoundaryWalls = candidateBoundaryWalls,
                Placements = placements
            };
        }

        private object ApplySmokeExhaustWindowPlan(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string planId = parameters["planId"]?.Value<string>();
            StoredSmokeWindowPlan plan = null;
            if (!string.IsNullOrWhiteSpace(planId))
            {
                lock (SmokePlanStateLock) SmokeWindowPlans.TryGetValue(planId, out plan);
                if (plan == null) throw new Exception("The smoke exhaust Window plan was not found or has already been applied.");
                if (DateTime.UtcNow - plan.CreatedAtUtc > TimeSpan.FromMinutes(30))
                {
                    lock (SmokePlanStateLock) SmokeWindowPlans.Remove(planId);
                    throw new Exception("The smoke exhaust Window plan expired; create a fresh preview before applying.");
                }
            }
            else
            {
                IdType roomId = parameters["roomId"]?.Value<IdType>() ?? 0;
                string windowType = parameters["windowType"]?.Value<string>();
                JArray rows = parameters["placements"] as JArray;
                if (roomId == 0 || string.IsNullOrWhiteSpace(windowType) || rows == null || rows.Count < 1)
                    throw new Exception("Provide planId or roomId, windowType, and at least one complete placement.");
                FamilySymbol symbol = ResolveSmokeWindowSymbol(doc, windowType);
                plan = new StoredSmokeWindowPlan
                {
                    PlanId = Guid.NewGuid().ToString("N"),
                    CreatedAtUtc = DateTime.UtcNow,
                    DocumentKey = GetSmokePlanDocumentKey(doc),
                    RoomId = roomId,
                    WindowType = symbol.FamilyName + ": " + symbol.Name,
                    TypeId = symbol.Id.GetIdValue(),
                    Count = rows.Count,
                    Placements = rows.Select(ParseSmokeWindowPlacement).ToList()
                };
            }

            if (plan.DocumentKey != GetSmokePlanDocumentKey(doc)) throw new Exception("The plan belongs to a different Revit document.");
            Room room = doc.GetElement(plan.RoomId.ToElementId()) as Room;
            if (room == null || room.Area <= 0) throw new Exception($"Room {plan.RoomId} no longer exists or is not placed.");
            FamilySymbol symbolNow = doc.GetElement(plan.TypeId.ToElementId()) as FamilySymbol;
            if (symbolNow == null || symbolNow.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Windows)
                throw new Exception("The planned Window Type no longer exists.");
            if (plan.Count < 1 || plan.Placements == null || plan.Placements.Count != plan.Count)
                throw new Exception("The plan placement count does not match its requested Window count.");
            ValidateSmokePlanPlacements(doc, room, symbolNow, plan.Placements);

            Level level = doc.GetElement(room.LevelId) as Level;
            if (level == null) throw new Exception("The Room level could not be resolved.");
            var createdIds = new List<IdType>();
            using (Transaction trans = new Transaction(doc, "Apply Smoke Exhaust Window Plan"))
            {
                trans.Start();
                try
                {
                    if (!symbolNow.IsActive) { symbolNow.Activate(); doc.Regenerate(); }
                    foreach (SmokeWindowPlacement placement in plan.Placements)
                    {
                        Wall wall = doc.GetElement(placement.HostWallId.ToElementId()) as Wall;
                        if (wall == null) throw new Exception($"Host Wall {placement.HostWallId} no longer exists.");
                        XYZ point = new XYZ(placement.LocationX / SmokePlanMmPerFoot, placement.LocationY / SmokePlanMmPerFoot,
                            level.Elevation + placement.SillHeightMm / SmokePlanMmPerFoot);
                        FamilyInstance window = doc.Create.NewFamilyInstance(point, symbolNow, wall, level, Autodesk.Revit.DB.Structure.StructuralType.NonStructural);
                        if (window == null) throw new Exception("Revit did not create a Window instance.");
                        Parameter sill = window.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                        if (sill == null || sill.IsReadOnly || !sill.Set(placement.SillHeightMm / SmokePlanMmPerFoot))
                            throw new Exception("The created Window could not be assigned the planned Sill Height.");
                        createdIds.Add(window.Id.GetIdValue());
                    }
                    trans.Commit();
                }
                catch
                {
                    if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                    throw;
                }
            }

            string operationId = Guid.NewGuid().ToString("N");
            var operation = new StoredSmokeWindowOperation
            {
                OperationId = operationId,
                DocumentKey = GetSmokePlanDocumentKey(doc),
                RoomId = plan.RoomId,
                WindowType = plan.WindowType,
                CreatedElementIds = createdIds,
                Placements = plan.Placements
            };
            lock (SmokePlanStateLock)
            {
                SmokeWindowOperations[operationId] = operation;
                SmokeWindowPlans.Remove(plan.PlanId);
            }
            return new { Success = true, OperationId = operationId, RoomId = plan.RoomId, CreatedElementIds = createdIds, Placements = plan.Placements, WindowType = plan.WindowType };
        }

        private object UndoSmokeExhaustWindowPlan(JObject parameters)
        {
            Document doc = _uiApp.ActiveUIDocument.Document;
            string operationId = parameters["operationId"]?.Value<string>();
            if (string.IsNullOrWhiteSpace(operationId)) throw new Exception("operationId is required.");
            StoredSmokeWindowOperation operation;
            lock (SmokePlanStateLock)
            {
                if (!SmokeWindowOperations.TryGetValue(operationId, out operation))
                    throw new Exception("The smoke exhaust Window operation was not found or has already been undone.");
            }
            if (operation.DocumentKey != GetSmokePlanDocumentKey(doc)) throw new Exception("The operation belongs to a different Revit document.");

            var present = new List<SmokeWindowDeleteSnapshot>();
            var missing = new List<IdType>();
            foreach (IdType id in operation.CreatedElementIds)
            {
                Element element = doc.GetElement(id.ToElementId());
                if (element == null) { missing.Add(id); continue; }
                IdType categoryId = element.Category?.Id.GetIdValue() ?? 0;
                if (categoryId != (IdType)(int)BuiltInCategory.OST_Windows)
                    throw new Exception($"Element {id} is no longer a Window; undo stopped without deleting anything.");
                FamilyInstance window = element as FamilyInstance;
                present.Add(new SmokeWindowDeleteSnapshot
                {
                    ElementId = id,
                    CategoryId = categoryId,
                    TypeId = element.GetTypeId()?.GetIdValue() ?? 0,
                    HostWallId = window?.Host?.Id.GetIdValue() ?? 0
                });
            }

            var deleted = new List<IdType>();
            if (present.Count > 0)
            {
                using (Transaction trans = new Transaction(doc, "Undo Smoke Exhaust Window Plan"))
                {
                    trans.Start();
                    try
                    {
                        foreach (SmokeWindowDeleteSnapshot snapshot in present)
                        {
                            ElementId elementId = snapshot.ElementId.ToElementId();
                            ICollection<ElementId> removed = doc.Delete(elementId);
                            if (!removed.Contains(elementId)) throw new Exception($"Revit did not delete planned Window {snapshot.ElementId}.");
                            deleted.Add(snapshot.ElementId);
                        }
                        foreach (SmokeWindowDeleteSnapshot snapshot in present)
                            if (doc.GetElement(snapshot.ElementId.ToElementId()) != null)
                                throw new Exception($"Planned Window {snapshot.ElementId} still exists after deletion; undo stopped.");
                        trans.Commit();
                    }
                    catch
                    {
                        if (trans.GetStatus() == TransactionStatus.Started) trans.RollBack();
                        throw;
                    }
                }
            }
            lock (SmokePlanStateLock) SmokeWindowOperations.Remove(operationId);
            return new { Success = true, DeletedElementIds = deleted, MissingElementIds = missing,
                DeletedWindows = present.Select(item => new { item.ElementId, item.CategoryId, item.TypeId, item.HostWallId }).ToList(),
                OperationId = operationId };
        }

        private static bool IsFinite(double value) => !double.IsNaN(value) && !double.IsInfinity(value);

        private string GetSmokePlanDocumentKey(Document doc)
        {
            return (doc.PathName ?? string.Empty) + "|" + (doc.ProjectInformation?.UniqueId ?? doc.GetHashCode().ToString());
        }

        private FamilySymbol ResolveSmokeWindowSymbol(Document doc, string requestedType)
        {
            var symbols = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Windows)
                .WhereElementIsElementType()
                .OfType<FamilySymbol>()
                .Where(symbol => string.Equals(symbol.FamilyName + ": " + symbol.Name, requestedType, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(symbol.Name, requestedType, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (symbols.Count != 1) throw new Exception(symbols.Count == 0
                ? $"Window Type '{requestedType}' was not found."
                : $"Window Type '{requestedType}' is ambiguous; use 'FamilyName: TypeName'.");
            return symbols[0];
        }

        private double GetSmokeWindowDimensionMm(FamilySymbol symbol, bool width)
        {
            BuiltInParameter[] bips = width
                ? new[] { BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.WINDOW_WIDTH }
                : new[] { BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.WINDOW_HEIGHT };
            string[] names = width ? new[] { "Width", "寬度" } : new[] { "Height", "高度" };
            return (GetParamValue(symbol, bips, names) ?? 0) * SmokePlanMmPerFoot;
        }

        private List<OuterWallSegment> GetOuterWallSegments(Document doc, Room room)
        {
            var loops = room.GetBoundarySegments(new SpatialElementBoundaryOptions());
            if (loops == null || loops.Count == 0) return new List<OuterWallSegment>();
            var measured = loops.Select(loop =>
            {
                var points = loop.Select(segment => segment.GetCurve().GetEndPoint(0)).ToList();
                double area2 = 0;
                for (int i = 0; i < points.Count; i++)
                {
                    XYZ a = points[i], b = points[(i + 1) % points.Count];
                    area2 += a.X * b.Y - b.X * a.Y;
                }
                return new { Loop = loop, Area = Math.Abs(area2 / 2.0) };
            }).OrderByDescending(item => item.Area).First();

            var result = new List<OuterWallSegment>();
            var roomWallIds = new Dictionary<IdType, List<RoomBoundaryReference>>();
            (double Bottom, double Top) selectedVerticalRange = GetSmokeRoomVerticalRangeFeet(doc, room);
            foreach (Room otherRoom in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType().OfType<Room>().Where(item => item.Area > 0 && item.Id != room.Id))
            {
                var otherLoops = otherRoom.GetBoundarySegments(new SpatialElementBoundaryOptions());
                if (otherLoops == null) continue;
                (double Bottom, double Top) otherVerticalRange = GetSmokeRoomVerticalRangeFeet(doc, otherRoom);
                bool sameLevelAndOverlap = otherRoom.LevelId == room.LevelId &&
                    Math.Min(selectedVerticalRange.Top, otherVerticalRange.Top) - Math.Max(selectedVerticalRange.Bottom, otherVerticalRange.Bottom) > 1.0 / SmokePlanMmPerFoot;
                Level otherLevel = doc.GetElement(otherRoom.LevelId) as Level;
                string label = $"{otherRoom.Number} {otherRoom.Name} (Level {otherLevel?.Name ?? "unknown"})".Trim();
                foreach (BoundarySegment otherBoundary in otherLoops.SelectMany(loop => loop))
                {
                    IdType otherWallId = otherBoundary.ElementId.GetIdValue();
                    Curve otherCurve = otherBoundary.GetCurve();
                    if (!(otherCurve is Line)) continue;
                    if (!roomWallIds.TryGetValue(otherWallId, out List<RoomBoundaryReference> references)) roomWallIds[otherWallId] = references = new List<RoomBoundaryReference>();
                    references.Add(new RoomBoundaryReference
                    {
                        Label = sameLevelAndOverlap ? label : $"{label}: ignored (different Level or no vertical overlap)",
                        SameLevelAndVerticalOverlap = sameLevelAndOverlap,
                        Start = otherCurve.GetEndPoint(0), End = otherCurve.GetEndPoint(1)
                    });
                }
            }
            var roomWindowsByWall = new HashSet<IdType>(new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Windows).WhereElementIsNotElementType()
                .OfType<FamilyInstance>().Where(window => window.Host is Wall &&
                    (TryGetFromRoom(window)?.Id == room.Id || TryGetToRoom(window)?.Id == room.Id))
                .Select(window => window.Host.Id.GetIdValue()));
            foreach (BoundarySegment boundary in measured.Loop)
            {
                Wall wall = doc.GetElement(boundary.ElementId) as Wall;
                Curve curve = boundary.GetCurve();
                if (wall == null || !(curve is Line)) continue;
                XYZ start = curve.GetEndPoint(0), end = curve.GetEndPoint(1);
                double lengthMm = Math.Sqrt(Math.Pow(end.X - start.X, 2) + Math.Pow(end.Y - start.Y, 2)) * SmokePlanMmPerFoot;
                if (lengthMm <= 0) continue;
                List<RoomBoundaryReference> referencesForWall = roomWallIds.TryGetValue(wall.Id.GetIdValue(), out List<RoomBoundaryReference> refs)
                    ? refs.Where(reference => GetCollinearOverlapLengthMm(start, end, reference.Start, reference.End) >= 50.0).ToList()
                    : new List<RoomBoundaryReference>();
                List<string> adjacentRooms = referencesForWall.Where(reference => reference.SameLevelAndVerticalOverlap).Select(reference => reference.Label).Distinct().ToList();
                List<string> ignoredOtherRooms = referencesForWall.Where(reference => !reference.SameLevelAndVerticalOverlap).Select(reference => reference.Label).Distinct().ToList();
                var segment = new OuterWallSegment
                {
                    WallId = wall.Id.GetIdValue(), Start = start, End = end, LengthMm = lengthMm,
                    IsExterior = wall.WallType?.Function == WallFunction.Exterior,
                    HasExistingRoomWindow = roomWindowsByWall.Contains(wall.Id.GetIdValue()),
                    AdjacentRooms = adjacentRooms,
                    IgnoredOtherRooms = ignoredOtherRooms
                };
                if (segment.IsExterior && segment.HasExistingRoomWindow)
                {
                    segment.IsValidExterior = true;
                    segment.ExteriorConfidenceRank = 3;
                    segment.ExteriorConfidence = "High";
                    segment.ExteriorValidationReason = "Accepted: existing room window host wall and WallType Function = Exterior";
                }
                else if (segment.IsExterior && segment.AdjacentRooms.Count == 0)
                {
                    segment.IsValidExterior = true;
                    segment.ExteriorConfidenceRank = 2;
                    segment.ExteriorConfidence = "High";
                    segment.ExteriorValidationReason = "Accepted: WallType Function = Exterior; no same-level vertically overlapping adjacent Room";
                }
                else if (segment.HasExistingRoomWindow && segment.AdjacentRooms.Count == 0)
                {
                    segment.IsValidExterior = true;
                    segment.ExteriorConfidenceRank = 1;
                    segment.ExteriorConfidence = "Medium";
                    segment.ExteriorValidationReason = "Accepted: existing room window host wall; no same-level vertically overlapping adjacent Room; WallType Function = Other";
                }
                else if (segment.AdjacentRooms.Count > 0)
                {
                    segment.ExteriorConfidenceRank = 0;
                    segment.ExteriorConfidence = "Rejected";
                    segment.ExteriorValidationReason = "Excluded: same-level vertically overlapping adjacent Room(s): " + string.Join(", ", segment.AdjacentRooms);
                }
                else
                {
                    segment.ExteriorConfidenceRank = 0;
                    segment.ExteriorConfidence = "Low";
                    segment.ExteriorValidationReason = "Low confidence candidate only: WallType Function = Other, no existing room window, and no same-level vertically overlapping adjacent Room; excluded from placement";
                }
                result.Add(segment);
            }
            return result;
        }

        private List<SmokeWindowPlacement> FindSmokeWindowPlacements(Document doc, List<OuterWallSegment> segments,
            double widthMm, double heightMm, double sillHeightMm, int count)
        {
            var placements = new List<SmokeWindowPlacement>();
            List<OuterWallSegment> orderedSegments = segments.OrderByDescending(item => item.ExteriorConfidenceRank)
                .ThenByDescending(item => item.LengthMm).ToList();
            double halfWidth = widthMm / 2.0;
            double step = Math.Max(50.0, Math.Min(100.0, widthMm / 8.0));

            while (placements.Count < count)
            {
                SmokeWindowPlacement bestPlacement = null;
                double bestScore = double.NegativeInfinity;
                foreach (OuterWallSegment segment in orderedSegments)
                {
                    Wall wall = doc.GetElement(segment.WallId.ToElementId()) as Wall;
                    if (wall == null) continue;
                    var obstacles = GetExistingWindowObstacles(doc, wall, segment);
                    double minCenter = halfWidth + SmokePlanEndClearanceMm;
                    double maxCenter = segment.LengthMm - minCenter;
                    if (maxCenter < minCenter) continue;

                    var distances = new List<double>();
                    for (double distance = minCenter; distance <= maxCenter; distance += step) distances.Add(distance);
                    if (distances.Count == 0 || maxCenter - distances[distances.Count - 1] > 1.0) distances.Add(maxCenter);
                    foreach (double distance in distances)
                    {
                        double t = distance / segment.LengthMm;
                        var candidate = new SmokeWindowPlacement
                        {
                            HostWallId = segment.WallId, ExteriorValidationReason = segment.ExteriorValidationReason,
                            LocationX = Math.Round((segment.Start.X + (segment.End.X - segment.Start.X) * t) * SmokePlanMmPerFoot, 2),
                            LocationY = Math.Round((segment.Start.Y + (segment.End.Y - segment.Start.Y) * t) * SmokePlanMmPerFoot, 2),
                            SillHeightMm = sillHeightMm, WidthMm = widthMm, HeightMm = heightMm
                        };
                        var proposedSet = placements.Concat(new[] { candidate }).ToList();
                        if (GetSmokeWindowPlacementValidationError(doc, segments, proposedSet) != null) continue;

                        double score = Math.Min(distance - minCenter, maxCenter - distance);
                        foreach (var obstacle in obstacles)
                            score = Math.Min(score, Math.Abs(distance - obstacle.DistanceMm) - halfWidth - obstacle.WidthMm / 2.0);
                        foreach (SmokeWindowPlacement prior in placements)
                        {
                            var priorSegment = segments.Where(item => item.WallId == prior.HostWallId)
                                .OrderBy(item => DistanceToSegmentMm(item, prior.LocationX, prior.LocationY)).FirstOrDefault();
                            if (priorSegment != null)
                                score = Math.Min(score, GetSmokeWindowFootprintClearDistance(candidate, segment, prior, priorSegment));
                        }
                        if (bestPlacement == null || score > bestScore)
                        {
                            bestPlacement = candidate;
                            bestScore = score;
                        }
                    }
                }
                if (bestPlacement == null) break;
                placements.Add(bestPlacement);
            }
            return placements;
        }

        private List<SmokeWindowPlacement> FindSmokeWindowDiagnosticPlacements(Document doc,
            List<OuterWallSegment> allSegments, List<SmokeWindowPlacement> validPlacements, double widthMm, double heightMm,
            double sillHeightMm, int missingCount)
        {
            var diagnostics = new List<SmokeWindowPlacement>();
            List<OuterWallSegment> segments = allSegments
                .Where(segment => segment.LengthMm > 0 && doc.GetElement(segment.WallId.ToElementId()) is Wall)
                .OrderByDescending(segment => segment.IsValidExterior)
                .ThenByDescending(segment => segment.LengthMm)
                .ToList();
            if (segments.Count == 0 || missingCount < 1) return diagnostics;

            double halfWidth = widthMm / 2.0;
            double step = Math.Max(100.0, Math.Min(300.0, widthMm / 8.0));
            var candidates = new List<(OuterWallSegment Segment, SmokeWindowPlacement Placement, double Distance)>();
            foreach (OuterWallSegment segment in segments)
            {
                double firstCenter = Math.Min(halfWidth, segment.LengthMm / 2.0);
                double lastCenter = Math.Max(segment.LengthMm - halfWidth, segment.LengthMm / 2.0);
                if (lastCenter < firstCenter) firstCenter = lastCenter = segment.LengthMm / 2.0;
                int sampleCount = Math.Max(1, (int)Math.Ceiling((lastCenter - firstCenter) / step));
                for (int sample = 0; sample <= sampleCount; sample++)
                {
                    double distance = firstCenter + (lastCenter - firstCenter) * sample / sampleCount;
                    double t = distance / segment.LengthMm;
                    candidates.Add((segment, new SmokeWindowPlacement
                    {
                        HostWallId = segment.WallId,
                        ExteriorValidationReason = segment.ExteriorValidationReason,
                        LocationX = Math.Round((segment.Start.X + (segment.End.X - segment.Start.X) * t) * SmokePlanMmPerFoot, 2),
                        LocationY = Math.Round((segment.Start.Y + (segment.End.Y - segment.Start.Y) * t) * SmokePlanMmPerFoot, 2),
                        SillHeightMm = sillHeightMm,
                        WidthMm = widthMm,
                        HeightMm = heightMm
                    }, distance));
                }
            }

            var prior = new List<SmokeWindowPlacement>(validPlacements ?? new List<SmokeWindowPlacement>());
            while (diagnostics.Count < missingCount && candidates.Count > 0)
            {
                (OuterWallSegment Segment, SmokeWindowPlacement Placement, double Distance) best = default;
                string[] bestReasons = null;
                double bestScore = double.PositiveInfinity;
                foreach (var candidate in candidates)
                {
                    string[] reasons = GetSmokeWindowDiagnosticConflictReasons(doc, candidate.Segment,
                        allSegments, candidate.Placement, candidate.Distance, prior);
                    double edgeClearance = Math.Min(candidate.Distance - halfWidth,
                        candidate.Segment.LengthMm - candidate.Distance - halfWidth);
                    double score = reasons.Length * 100000.0 - edgeClearance;
                    if (score < bestScore)
                    {
                        best = candidate;
                        bestReasons = reasons;
                        bestScore = score;
                    }
                }

                if (best.Placement == null) break;
                if (bestReasons == null || bestReasons.Length == 0)
                    bestReasons = new[] { "未能通過正式配置搜尋，僅供診斷預覽" };
                best.Placement.ConflictReasons = bestReasons.ToList();
                diagnostics.Add(best.Placement);
                prior.Add(best.Placement);
                candidates.RemoveAll(candidate => candidate.Segment.WallId == best.Segment.WallId &&
                    Math.Abs(candidate.Distance - best.Distance) < step / 2.0);
            }
            return diagnostics;
        }

        private string[] GetSmokeWindowDiagnosticConflictReasons(Document doc, OuterWallSegment segment,
            List<OuterWallSegment> allSegments, SmokeWindowPlacement placement, double distance,
            IList<SmokeWindowPlacement> priorPlacements)
        {
            var reasons = new List<string>();
            double halfWidth = placement.WidthMm / 2.0;
            double startClear = distance - halfWidth;
            double endClear = segment.LengthMm - distance - halfWidth;
            if (segment.LengthMm < placement.WidthMm + 2.0 * SmokePlanEndClearanceMm)
                reasons.Add("可用牆段不足");
            if (startClear < SmokePlanEndClearanceMm || endClear < SmokePlanEndClearanceMm)
                reasons.Add("距離牆端不足");
            if (!segment.IsValidExterior)
                reasons.Add(segment.AdjacentRooms.Count > 0
                    ? "無足夠外牆位置（宿主牆與其他房間相鄰）"
                    : "無足夠外牆位置（宿主牆未確認為外牆）");

            Wall wall = doc.GetElement(segment.WallId.ToElementId()) as Wall;
            if (wall != null)
            {
                foreach (var obstacle in GetExistingWindowObstacles(doc, wall, segment))
                {
                    double clearDistance = Math.Abs(distance - obstacle.DistanceMm) - halfWidth - obstacle.WidthMm / 2.0;
                    if (clearDistance < SmokePlanEndClearanceMm)
                    {
                        reasons.Add("與既有窗距離不足");
                        break;
                    }
                }
            }

            foreach (SmokeWindowPlacement prior in priorPlacements ?? new List<SmokeWindowPlacement>())
            {
                OuterWallSegment priorSegment = (allSegments ?? new List<OuterWallSegment>())
                    .Where(item => item.WallId == prior.HostWallId)
                    .OrderBy(item => DistanceToSegmentMm(item, prior.LocationX, prior.LocationY))
                    .FirstOrDefault();
                if (priorSegment == null)
                {
                    priorSegment = new[] { segment }.Where(item => item.WallId == prior.HostWallId).FirstOrDefault();
                }
                if (priorSegment != null && GetSmokeWindowFootprintClearDistance(placement, segment, prior, priorSegment) < SmokePlanEndClearanceMm)
                {
                    reasons.Add("與另一候選窗間距不足");
                    break;
                }
            }
            return reasons.Distinct().ToArray();
        }

        private List<(double DistanceMm, double WidthMm)> GetExistingWindowObstacles(Document doc, Wall wall, OuterWallSegment segment)
        {
            var obstacles = new List<(double DistanceMm, double WidthMm)>();
            foreach (ElementId id in wall.FindInserts(true, false, false, false))
            {
                FamilyInstance window = doc.GetElement(id) as FamilyInstance;
                if (window?.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Windows) continue;
                LocationPoint point = window.Location as LocationPoint;
                if (point == null) continue;
                XYZ p = point.Point;
                double px = p.X * SmokePlanMmPerFoot, py = p.Y * SmokePlanMmPerFoot;
                double distance = ProjectDistanceMm(segment, px, py);
                double width = GetSmokeWindowDimensionMm(window.Symbol, true);
                if (distance >= -width && distance <= segment.LengthMm + width && width > 0)
                    obstacles.Add((distance, width));
            }
            return obstacles;
        }

        private double ProjectDistanceMm(OuterWallSegment segment, double xMm, double yMm)
        {
            double sx = segment.Start.X * SmokePlanMmPerFoot, sy = segment.Start.Y * SmokePlanMmPerFoot;
            double dx = (segment.End.X - segment.Start.X) * SmokePlanMmPerFoot;
            double dy = (segment.End.Y - segment.Start.Y) * SmokePlanMmPerFoot;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length <= 0) return 0;
            return ((xMm - sx) * dx + (yMm - sy) * dy) / length;
        }

        private SmokeWindowPlacement ParseSmokeWindowPlacement(JToken token)
        {
            return new SmokeWindowPlacement
            {
                HostWallId = token["hostWallId"]?.Value<IdType>() ?? token["HostWallId"]?.Value<IdType>() ?? 0,
                LocationX = token["locationX"]?.Value<double>() ?? token["LocationX"]?.Value<double>() ?? double.NaN,
                LocationY = token["locationY"]?.Value<double>() ?? token["LocationY"]?.Value<double>() ?? double.NaN,
                SillHeightMm = token["sillHeightMm"]?.Value<double>() ?? token["SillHeightMm"]?.Value<double>() ?? double.NaN,
                WidthMm = token["widthMm"]?.Value<double>() ?? token["WidthMm"]?.Value<double>() ?? double.NaN,
                HeightMm = token["heightMm"]?.Value<double>() ?? token["HeightMm"]?.Value<double>() ?? double.NaN
            };
        }

        private void ValidateSmokePlanPlacements(Document doc, Room room, FamilySymbol symbol, List<SmokeWindowPlacement> placements)
        {
            List<OuterWallSegment> outerSegments = GetOuterWallSegments(doc, room);
            double actualWidth = GetSmokeWindowDimensionMm(symbol, true);
            double actualHeight = GetSmokeWindowDimensionMm(symbol, false);
            if (actualWidth <= 0 || actualHeight <= 0) throw new Exception("The selected Window Type no longer has valid dimensions.");
            double zoneTopMm = GetCeilingHeight(doc, room, "room_parameter");
            double zoneBottomMm = zoneTopMm - 800.0;
            foreach (SmokeWindowPlacement placement in placements ?? new List<SmokeWindowPlacement>())
            {
                if (placement.HostWallId == 0 || !IsFinite(placement.LocationX) || !IsFinite(placement.LocationY) ||
                    !IsFinite(placement.SillHeightMm) || placement.SillHeightMm < 0 ||
                    Math.Abs(placement.WidthMm - actualWidth) > 2.0 || Math.Abs(placement.HeightMm - actualHeight) > 2.0)
                    throw new Exception("A placement has invalid coordinates, sill height, or dimensions that do not match the Window Type.");
                if (Math.Min(placement.SillHeightMm + actualHeight, zoneTopMm) <= Math.Max(placement.SillHeightMm, zoneBottomMm))
                    throw new Exception("A placement does not intersect the Room's effective smoke zone.");
            }
            string placementError = GetSmokeWindowPlacementValidationError(doc, outerSegments, placements);
            if (placementError != null) throw new Exception(placementError);
        }

        private string GetSmokeWindowPlacementValidationError(Document doc, List<OuterWallSegment> outerSegments,
            IList<SmokeWindowPlacement> placements)
        {
            if (placements == null) return "The planned Window placements are missing.";
            var mapped = new List<(int Index, SmokeWindowPlacement Placement, OuterWallSegment Segment, double Distance)>();
            for (int i = 0; i < placements.Count; i++)
            {
                SmokeWindowPlacement placement = placements[i];
                var match = outerSegments.Where(item => item.WallId == placement.HostWallId && item.IsValidExterior)
                    .Select(item => new { Segment = item, Distance = ProjectDistanceMm(item, placement.LocationX, placement.LocationY),
                        Offset = DistanceToSegmentMm(item, placement.LocationX, placement.LocationY) })
                    .Where(item => item.Offset <= 10.0 && item.Distance >= -1.0 && item.Distance <= item.Segment.LengthMm + 1.0)
                    .OrderBy(item => item.Offset).FirstOrDefault();
                if (match == null)
                    return $"Placement {i + 1} is not on a valid exterior boundary segment hosted by Wall {placement.HostWallId}.";

                double halfWidth = placement.WidthMm / 2.0;
                double startClear = match.Distance - halfWidth;
                double endClear = match.Segment.LengthMm - match.Distance - halfWidth;
                if (startClear < SmokePlanEndClearanceMm || endClear < SmokePlanEndClearanceMm)
                    return $"Placement {i + 1} is too close to a wall end/corner: StartClear={startClear:F2} mm, EndClear={endClear:F2} mm; minimum={SmokePlanEndClearanceMm:F2} mm.";

                Wall wall = doc.GetElement(placement.HostWallId.ToElementId()) as Wall;
                if (wall == null || wall.Category?.Id.GetIdValue() != (IdType)(int)BuiltInCategory.OST_Walls)
                    return $"Host Wall {placement.HostWallId} no longer exists.";
                foreach (var obstacle in GetExistingWindowObstacles(doc, wall, match.Segment))
                {
                    double centerDistance = Math.Abs(match.Distance - obstacle.DistanceMm);
                    double clearDistance = centerDistance - halfWidth - obstacle.WidthMm / 2.0;
                    if (clearDistance < SmokePlanEndClearanceMm)
                        return $"Placement {i + 1} conflicts with an existing Window on Wall {placement.HostWallId}: CenterDistance={centerDistance:F2} mm, ClearDistance={clearDistance:F2} mm; minimum={SmokePlanEndClearanceMm:F2} mm.";
                }
                mapped.Add((i, placement, match.Segment, match.Distance));
            }

            for (int i = 0; i < mapped.Count; i++)
            for (int j = i + 1; j < mapped.Count; j++)
            {
                var first = mapped[i];
                var second = mapped[j];
                double centerDistance = Math.Sqrt(Math.Pow(first.Placement.LocationX - second.Placement.LocationX, 2) +
                    Math.Pow(first.Placement.LocationY - second.Placement.LocationY, 2));
                double clearDistance = GetSmokeWindowFootprintClearDistance(first.Placement, first.Segment, second.Placement, second.Segment);
                if (clearDistance < SmokePlanEndClearanceMm)
                {
                    string relation = first.Placement.HostWallId == second.Placement.HostWallId ? "same Host Wall" : "different Host Walls / corner";
                    return $"The planned Window placements overlap or are too close ({relation}): Placement {first.Index + 1} and {second.Index + 1}, CenterDistance={centerDistance:F2} mm, ClearDistance={clearDistance:F2} mm; minimum={SmokePlanEndClearanceMm:F2} mm.";
                }
            }
            return null;
        }

        private double GetSmokeWindowFootprintClearDistance(SmokeWindowPlacement first, OuterWallSegment firstSegment,
            SmokeWindowPlacement second, OuterWallSegment secondSegment)
        {
            double firstDx = (firstSegment.End.X - firstSegment.Start.X) * SmokePlanMmPerFoot;
            double firstDy = (firstSegment.End.Y - firstSegment.Start.Y) * SmokePlanMmPerFoot;
            double firstLength = Math.Sqrt(firstDx * firstDx + firstDy * firstDy);
            double secondDx = (secondSegment.End.X - secondSegment.Start.X) * SmokePlanMmPerFoot;
            double secondDy = (secondSegment.End.Y - secondSegment.Start.Y) * SmokePlanMmPerFoot;
            double secondLength = Math.Sqrt(secondDx * secondDx + secondDy * secondDy);
            if (firstLength <= 0 || secondLength <= 0) return 0;

            double firstHalf = first.WidthMm / 2.0, secondHalf = second.WidthMm / 2.0;
            var firstA = (X: first.LocationX - firstDx / firstLength * firstHalf, Y: first.LocationY - firstDy / firstLength * firstHalf);
            var firstB = (X: first.LocationX + firstDx / firstLength * firstHalf, Y: first.LocationY + firstDy / firstLength * firstHalf);
            var secondA = (X: second.LocationX - secondDx / secondLength * secondHalf, Y: second.LocationY - secondDy / secondLength * secondHalf);
            var secondB = (X: second.LocationX + secondDx / secondLength * secondHalf, Y: second.LocationY + secondDy / secondLength * secondHalf);
            return DistanceBetweenSmokePlanSegments(firstA, firstB, secondA, secondB);
        }

        private double DistanceBetweenSmokePlanSegments((double X, double Y) a, (double X, double Y) b,
            (double X, double Y) c, (double X, double Y) d)
        {
            double Cross((double X, double Y) p, (double X, double Y) q, (double X, double Y) r) =>
                (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
            double c1 = Cross(a, b, c), c2 = Cross(a, b, d), c3 = Cross(c, d, a), c4 = Cross(c, d, b);
            bool OnSegment((double X, double Y) p, (double X, double Y) start, (double X, double Y) end) =>
                p.X >= Math.Min(start.X, end.X) - 1e-8 && p.X <= Math.Max(start.X, end.X) + 1e-8 &&
                p.Y >= Math.Min(start.Y, end.Y) - 1e-8 && p.Y <= Math.Max(start.Y, end.Y) + 1e-8;
            bool properIntersection = ((c1 < 0 && c2 > 0) || (c1 > 0 && c2 < 0)) &&
                ((c3 < 0 && c4 > 0) || (c3 > 0 && c4 < 0));
            if (properIntersection || (Math.Abs(c1) <= 1e-8 && OnSegment(c, a, b)) ||
                (Math.Abs(c2) <= 1e-8 && OnSegment(d, a, b)) || (Math.Abs(c3) <= 1e-8 && OnSegment(a, c, d)) ||
                (Math.Abs(c4) <= 1e-8 && OnSegment(b, c, d))) return 0;

            double PointToSegment((double X, double Y) p, (double X, double Y) start, (double X, double Y) end)
            {
                double dx = end.X - start.X, dy = end.Y - start.Y;
                double lengthSquared = dx * dx + dy * dy;
                if (lengthSquared <= 1e-12) return Math.Sqrt(Math.Pow(p.X - start.X, 2) + Math.Pow(p.Y - start.Y, 2));
                double t = Math.Max(0, Math.Min(1, ((p.X - start.X) * dx + (p.Y - start.Y) * dy) / lengthSquared));
                double x = start.X + t * dx, y = start.Y + t * dy;
                return Math.Sqrt(Math.Pow(p.X - x, 2) + Math.Pow(p.Y - y, 2));
            }

            return Math.Min(Math.Min(PointToSegment(a, c, d), PointToSegment(b, c, d)),
                Math.Min(PointToSegment(c, a, b), PointToSegment(d, a, b)));
        }

        private double DistanceToSegmentMm(OuterWallSegment segment, double xMm, double yMm)
        {
            double distance = ProjectDistanceMm(segment, xMm, yMm);
            double t = Math.Max(0, Math.Min(segment.LengthMm, distance));
            double px = segment.Start.X * SmokePlanMmPerFoot + (segment.End.X - segment.Start.X) * SmokePlanMmPerFoot * t / segment.LengthMm;
            double py = segment.Start.Y * SmokePlanMmPerFoot + (segment.End.Y - segment.Start.Y) * SmokePlanMmPerFoot * t / segment.LengthMm;
            return Math.Sqrt(Math.Pow(xMm - px, 2) + Math.Pow(yMm - py, 2));
        }

        private (double Bottom, double Top) GetSmokeRoomVerticalRangeFeet(Document doc, Room room)
        {
            Level baseLevel = doc.GetElement(room.LevelId) as Level;
            double baseElevation = baseLevel?.Elevation ?? 0;
            double lowerOffset = room.get_Parameter(BuiltInParameter.ROOM_LOWER_OFFSET)?.AsDouble() ?? 0;
            double bottom = baseElevation + lowerOffset;
            Parameter upperLimit = room.get_Parameter(BuiltInParameter.ROOM_UPPER_LEVEL);
            Parameter upperOffset = room.get_Parameter(BuiltInParameter.ROOM_UPPER_OFFSET);
            double top = double.NaN;
            if (upperLimit != null && upperOffset != null && upperLimit.AsElementId() != ElementId.InvalidElementId)
            {
                Level upperLevel = doc.GetElement(upperLimit.AsElementId()) as Level;
                if (upperLevel != null) top = upperLevel.Elevation + upperOffset.AsDouble();
            }
            if (double.IsNaN(top) || top <= bottom)
                top = baseElevation + GetCeilingHeight(doc, room, "room_parameter") / SmokePlanMmPerFoot;
            BoundingBoxXYZ bounds = room.get_BoundingBox(null);
            if (bounds != null)
            {
                bottom = Math.Max(bottom, bounds.Min.Z);
                top = Math.Min(top, bounds.Max.Z);
            }
            return (bottom, Math.Max(bottom, top));
        }

        private double GetCollinearOverlapLengthMm(XYZ a0, XYZ a1, XYZ b0, XYZ b1)
        {
            double dx = a1.X - a0.X, dy = a1.Y - a0.Y;
            double length = Math.Sqrt(dx * dx + dy * dy);
            if (length < 1e-9) return 0;
            double ux = dx / length, uy = dy / length;
            double perpendicular0 = Math.Abs((b0.X - a0.X) * uy - (b0.Y - a0.Y) * ux);
            double perpendicular1 = Math.Abs((b1.X - a0.X) * uy - (b1.Y - a0.Y) * ux);
            if (perpendicular0 > 0.1 || perpendicular1 > 0.1) return 0;
            double p0 = (b0.X - a0.X) * ux + (b0.Y - a0.Y) * uy;
            double p1 = (b1.X - a0.X) * ux + (b1.Y - a0.Y) * uy;
            double overlapFeet = Math.Max(0, Math.Min(length, Math.Max(p0, p1)) - Math.Max(0, Math.Min(p0, p1)));
            return overlapFeet * SmokePlanMmPerFoot;
        }
    }
}
