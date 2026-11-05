using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Analysis;
using Autodesk.Revit.DB.Architecture;
using Newtonsoft.Json.Linq;

#if REVIT2025_OR_GREATER
using IdType = System.Int64;
#else
using IdType = System.Int32;
#endif

namespace RevitMCP.Core
{
    /// <summary>
    /// 避難路徑檢核服務 (Egress Audit Service)
    /// </summary>
    public class EgressAuditService
    {
        // 固定共用參數 GUID
        public static readonly Guid GuidActualDist = new Guid("A1B2C3D4-1111-4000-8000-000000000001");
        public static readonly Guid GuidMaxAllowed = new Guid("A1B2C3D4-2222-4000-8000-000000000002");
        public static readonly Guid GuidIsCompliant = new Guid("A1B2C3D4-3333-4000-8000-000000000003");
        public static readonly Guid GuidTargetExit = new Guid("A1B2C3D4-4444-4000-8000-000000000004");

        // 固定共用參數名稱
        public const string ParamActualDist = "Egress_Actual_Dist";
        public const string ParamMaxAllowed = "Egress_Max_Allowed";
        public const string ParamIsCompliant = "Egress_Is_Compliant";
        public const string ParamTargetExit = "Egress_Target_Exit";

        // 8 方位單位向量（供頂點向心內縮失敗時的放射搜尋備援）
        private static readonly XYZ[] EightDirections = new XYZ[]
        {
            new XYZ(1, 0, 0),
            new XYZ(Math.Sqrt(0.5), Math.Sqrt(0.5), 0),
            new XYZ(0, 1, 0),
            new XYZ(-Math.Sqrt(0.5), Math.Sqrt(0.5), 0),
            new XYZ(-1, 0, 0),
            new XYZ(-Math.Sqrt(0.5), -Math.Sqrt(0.5), 0),
            new XYZ(0, -1, 0),
            new XYZ(Math.Sqrt(0.5), -Math.Sqrt(0.5), 0)
        };

        private class EgressSharedParamSpec
        {
            public string Name { get; set; } = string.Empty;
            public Guid Guid { get; set; }
            public ForgeTypeId DataType { get; set; } = SpecTypeId.String.Text;
            public ForgeTypeId GroupType { get; set; } = GroupTypeId.Data;
        }

        private class ExitDoorTarget
        {
            public long DoorId { get; set; }
            public ElementId ElementId { get; set; } = ElementId.InvalidElementId;
            public string Label { get; set; } = string.Empty;
            public XYZ Point { get; set; } = XYZ.Zero;
        }

        private class CandidateEgressPath
        {
            public PathOfTravel Path { get; set; } = null!;
            public int StartIndex { get; set; }
            public int ExitIndex { get; set; }
            public double LengthFeet { get; set; }
        }

        private class ExitEvaluation
        {
            public int ExitIndex { get; set; }
            public int CoveredVertexCount { get; set; }
            public CandidateEgressPath WorstPath { get; set; } = null!;
        }

        private static readonly IReadOnlyList<EgressSharedParamSpec> RoomSharedParamSpecs = new List<EgressSharedParamSpec>
        {
            new EgressSharedParamSpec
            {
                Name = ParamActualDist,
                Guid = GuidActualDist,
                DataType = SpecTypeId.Length,
                GroupType = GroupTypeId.Geometry
            },
            new EgressSharedParamSpec
            {
                Name = ParamMaxAllowed,
                Guid = GuidMaxAllowed,
                DataType = SpecTypeId.Length,
                GroupType = GroupTypeId.Geometry
            },
            new EgressSharedParamSpec
            {
                Name = ParamIsCompliant,
                Guid = GuidIsCompliant,
                DataType = SpecTypeId.String.Text,
                GroupType = GroupTypeId.Data
            },
            new EgressSharedParamSpec
            {
                Name = ParamTargetExit,
                Guid = GuidTargetExit,
                DataType = SpecTypeId.String.Text,
                GroupType = GroupTypeId.IdentityData
            }
        };

        /// <summary>
        /// 自動檢查並在房間（OST_Rooms）品類建立 4 個避難檢核共用參數。
        /// </summary>
        public static void EnsureRoomSharedParameters(Document doc)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));

            // 先檢查是否所有參數皆已存在
            bool allExist = true;
            foreach (var spec in RoomSharedParamSpecs)
            {
                if (SharedParameterElement.Lookup(doc, spec.Guid) == null)
                {
                    allExist = false;
                    break;
                }
            }

            if (allExist)
                return;

            var app = doc.Application;
            string originalSharedParametersFilename = app.SharedParametersFilename;
            string tempFilePath = Path.Combine(
                Path.GetTempPath(),
                $"RevitMCP_EgressParams_{Guid.NewGuid():N}.txt");

            try
            {
                File.WriteAllText(tempFilePath, string.Empty);
                app.SharedParametersFilename = tempFilePath;

                DefinitionFile defFile = app.OpenSharedParameterFile();
                if (defFile == null)
                    throw new InvalidOperationException($"無法開啟臨時共用參數檔: {tempFilePath}");

                DefinitionGroup defGroup = defFile.Groups.get_Item("EgressAudit")
                    ?? defFile.Groups.Create("EgressAudit");

                Category roomCategory = Category.GetCategory(doc, BuiltInCategory.OST_Rooms);
                if (roomCategory == null)
                    throw new InvalidOperationException("找不到房間品類 (OST_Rooms)");

                CategorySet categorySet = app.Create.NewCategorySet();
                categorySet.Insert(roomCategory);

                bool ownTransaction = !doc.IsModifiable;
                Transaction? trans = ownTransaction ? new Transaction(doc, "建立避難檢核房間共用參數") : null;

                try
                {
                    trans?.Start();

                    foreach (var spec in RoomSharedParamSpecs)
                    {
                        // 1. 先用 SharedParameterElement.Lookup(doc, guid) 檢查是否已存在，若已存在則跳過
                        SharedParameterElement existingElement = SharedParameterElement.Lookup(doc, spec.Guid);
                        if (existingElement != null)
                            continue;

                        // 2. 建立外部參數定義（指定固定 GUID 與型別）
                        var options = new ExternalDefinitionCreationOptions(spec.Name, spec.DataType)
                        {
                            GUID = spec.Guid,
                            Visible = true,
                            UserModifiable = true
                        };

                        Definition externalDef = defGroup.Definitions.Create(options);

                        // 3. 綁定為 InstanceBinding 至 OST_Rooms
                        InstanceBinding instanceBinding = app.Create.NewInstanceBinding(categorySet);
                        bool bound = doc.ParameterBindings.Insert(externalDef, instanceBinding, spec.GroupType);
                        if (!bound)
                        {
                            doc.ParameterBindings.ReInsert(externalDef, instanceBinding, spec.GroupType);
                        }

                        // 4. 透過 InternalDefinition.SetAllowVaryBetweenGroups(doc, true) 開啟「值可以依群組實例而異」
                        //    注意：Revit API 中 SpecTypeId.Length 等長度型別不支援 AllowVaryBetweenGroups，需捕獲 ArgumentException
                        SharedParameterElement createdElement = SharedParameterElement.Lookup(doc, spec.Guid);
                        InternalDefinition? internalDef = createdElement?.GetDefinition();
                        if (internalDef != null)
                        {
                            try
                            {
                                internalDef.SetAllowVaryBetweenGroups(doc, true);
                            }
                            catch (Autodesk.Revit.Exceptions.ArgumentException)
                            {
                                // SpecTypeId.Length 不支援 VaryBetweenGroups，略過即可
                            }
                        }
                    }

                    trans?.Commit();
                }
                catch
                {
                    if (trans != null && trans.HasStarted() && !trans.HasEnded())
                    {
                        trans.RollBack();
                    }
                    throw;
                }
                finally
                {
                    trans?.Dispose();
                }
            }
            finally
            {
                // 確保無論成功或失敗都會還原 app.SharedParametersFilename 並清理暫存檔
                try
                {
                    app.SharedParametersFilename = originalSharedParametersFilename ?? string.Empty;
                }
                catch
                {
                    // 忽略原路徑無效時的還原例外
                }

                try
                {
                    if (File.Exists(tempFilePath))
                    {
                        File.Delete(tempFilePath);
                    }
                }
                catch
                {
                    // 忽略暫存檔刪除失敗
                }
            }
        }

        /// <summary>
        /// 從 JSON 參數解析並執行避難路徑檢核（供 CommandExecutor 呼叫）
        /// </summary>
        public static object ExecuteEgressAudit(Document doc, JObject parameters)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (parameters == null)
                throw new ArgumentNullException(nameof(parameters));

            List<long> roomIds = parameters["roomIds"]?.Values<long>().ToList() ?? new List<long>();
            List<long> exitDoorIds = parameters["exitDoorIds"]?.Values<long>().ToList() ?? new List<long>();
            double maxAllowedMeters = parameters["maxAllowedMeters"]?.Value<double>() ?? 30.0;
            bool dimensionSegments = parameters["dimensionSegments"]?.Value<bool>() ?? true;
            string? occupancyCategory = parameters["occupancyCategory"]?.Value<string>();

            return ExecuteEgressAudit(doc, roomIds, exitDoorIds, maxAllowedMeters, dimensionSegments, occupancyCategory);
        }

        /// <summary>
        /// 執行房間避難路徑檢核 (audit_room_egress)
        /// </summary>
        public static object ExecuteEgressAudit(
            Document doc,
            IList<long> roomIds,
            IList<long> exitDoorIds,
            double maxAllowedMeters,
            bool dimensionSegments = true,
            string? occupancyCategory = null)
        {
            if (doc == null)
                throw new ArgumentNullException(nameof(doc));
            if (roomIds == null || roomIds.Count == 0)
                throw new ArgumentException("請提供至少一個房間 ID (roomIds)");
            if (exitDoorIds == null || exitDoorIds.Count == 0)
                throw new ArgumentException("請提供至少一個出口門 ID (exitDoorIds)");
            if (maxAllowedMeters <= 0)
                throw new ArgumentException("法規容許上限 (maxAllowedMeters) 必須大於 0");

            double maxAllowedFeet = UnitUtils.ConvertToInternalUnits(maxAllowedMeters, UnitTypeId.Meters);
            double insetFeet = UnitUtils.ConvertToInternalUnits(0.3, UnitTypeId.Meters);
            double minSegmentFeet = UnitUtils.ConvertToInternalUnits(0.1, UnitTypeId.Meters); // 最小標註線段門檻 10cm (0.1m)
            HashSet<long> targetExitDoorIdSet = new HashSet<long>(exitDoorIds);

            // 解析所有出口門元素與座標（步驟 1：將出口門目標點向室內側微幅內縮，確保終點位於室內空間）
            var exitTargets = new List<ExitDoorTarget>();
            foreach (long rawDoorId in exitDoorIds)
            {
                ElementId doorElemId = ((IdType)rawDoorId).ToElementId();
                Element doorElem = doc.GetElement(doorElemId);
                if (doorElem == null)
                    continue;

                XYZ? doorPt = GetInteriorDoorTargetPoint(doc, doorElem, insetFeet);
                if (doorPt == null)
                    continue;

                string mark = doorElem.get_Parameter(BuiltInParameter.ALL_MODEL_MARK)?.AsString() ?? string.Empty;
                string label = !string.IsNullOrWhiteSpace(mark)
                    ? $"{mark} (ID:{rawDoorId})"
                    : $"{doorElem.Name} (ID:{rawDoorId})";

                exitTargets.Add(new ExitDoorTarget
                {
                    DoorId = rawDoorId,
                    ElementId = doorElemId,
                    Label = label,
                    Point = doorPt
                });
            }

            if (exitTargets.Count == 0)
                throw new InvalidOperationException("無法從 exitDoorIds 解析出任何有效的出口門座標");

            ElementId dashPatternId = FindDashLinePatternId(doc);
            var roomResults = new List<object>();
            int compliantCount = 0;
            int nonCompliantCount = 0;
            int failedCount = 0;
            string resolvedLevelName = string.Empty;
            double resolvedLevelElevationMeters = 0.0;
            long resolvedViewId = 0;
            string resolvedViewName = string.Empty;
            long resolvedScheduleId = 0;
            string resolvedScheduleName = EgressSheetNoteService.DefaultScheduleName;
            List<string> resolvedScheduleFields = new List<string>();
            var assignedRoomCounts = new Dictionary<long, int>();

            string resolvedOccupancyCategory = !string.IsNullOrWhiteSpace(occupancyCategory)
                ? occupancyCategory!
                : (maxAllowedMeters <= 30.0
                    ? "第 93 條第 1 款（特定高風險場所／非防火構造）"
                    : (maxAllowedMeters <= 40.0
                        ? "第 93 條第 2 款（醫療／住宿／無窗居室／特定場所）"
                        : "第 93 條第 3 款（一般防火構造居室）"));

            // 預先收集有效 Room 與對應平面視圖，以便統一建立非目標外門臨時阻斷牆與清理舊路徑
            var validRooms = new List<Room>();
            ViewPlan? primaryViewPlan = null;
            foreach (long rawRoomId in roomIds)
            {
                Room? r = doc.GetElement(((IdType)rawRoomId).ToElementId()) as Room;
                if (r != null && r.Area > 0)
                {
                    validRooms.Add(r);
                    if (primaryViewPlan == null)
                    {
                        primaryViewPlan = ResolveFloorPlanForRoom(doc, r);
                    }
                }
            }

            using (TransactionGroup tg = new TransactionGroup(doc, "房間避難路徑檢核"))
            {
                tg.Start();

                // 1. 先確保 OST_Rooms 的 4 個共用參數已建立並綁定
                EnsureRoomSharedParameters(doc);

                // 2. 清理受檢房間先前的舊 PathOfTravel 與分段標註輔助線，並於非目標建築外門建立臨時阻斷牆（步驟 2）
                List<ElementId> tempBarrierWallIds = new List<ElementId>();
                List<XYZ> levelDoorCenters = new List<XYZ>();
                if (primaryViewPlan != null)
                {
                    double primaryElevation = primaryViewPlan.GenLevel != null
                        ? primaryViewPlan.GenLevel.Elevation
                        : (validRooms.Count > 0
                            ? (doc.GetElement(validRooms[0].LevelId) as Level)?.Elevation ?? 0.0
                            : 0.0);

                    using (Transaction prepTrans = new Transaction(doc, "準備室內避難尋路環境"))
                    {
                        prepTrans.Start();
                        var fOpts = prepTrans.GetFailureHandlingOptions();
                        fOpts.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                        fOpts.SetClearAfterRollback(true);
                        fOpts.SetForcedModalHandling(false);
                        prepTrans.SetFailureHandlingOptions(fOpts);

                        CleanupPreviousRoomAuditElements(doc, primaryViewPlan, validRooms, primaryElevation);
                        tempBarrierWallIds = CreateTemporaryExteriorDoorBarriers(
                            doc,
                            primaryViewPlan,
                            targetExitDoorIdSet,
                            primaryElevation,
                            out levelDoorCenters);
                        doc.Regenerate();
                        prepTrans.Commit();
                    }
                }

                try
                {
                    // 3. 逐一檢核每個 Room
                    foreach (long rawRoomId in roomIds)
                    {
                        ElementId roomElemId = ((IdType)rawRoomId).ToElementId();
                        Room? room = doc.GetElement(roomElemId) as Room;

                        if (room == null || room.Area <= 0)
                        {
                            failedCount++;
                            roomResults.Add(new
                            {
                                RoomId = rawRoomId,
                                Success = false,
                                Error = room == null ? "找不到該房間元素" : "房間未封閉或面積為 0"
                            });
                            continue;
                        }

                        ViewPlan? viewPlan = ResolveFloorPlanForRoom(doc, room);
                        if (viewPlan == null)
                        {
                            failedCount++;
                            roomResults.Add(new
                            {
                                RoomId = rawRoomId,
                                RoomName = room.Name,
                                Success = false,
                                Error = "找不到對應樓層的平面視圖 (ViewPlan)"
                            });
                            continue;
                        }

                        double planElevation = viewPlan.GenLevel != null
                            ? viewPlan.GenLevel.Elevation
                            : (doc.GetElement(room.LevelId) as Level)?.Elevation ?? 0.0;

                        if (resolvedViewId == 0)
                        {
                            resolvedViewId = viewPlan.Id.GetIdValue();
                            resolvedViewName = viewPlan.Name ?? string.Empty;
                            Level? lvl = viewPlan.GenLevel ?? (doc.GetElement(room.LevelId) as Level);
                            resolvedLevelName = lvl?.Name ?? resolvedViewName;
                            resolvedLevelElevationMeters = Math.Round(
                                UnitUtils.ConvertFromInternalUnits(planElevation, UnitTypeId.Meters), 2);
                        }

                        List<XYZ> startPoints = GetInsetRoomBoundaryVertices(room, insetFeet, planElevation);
                        if (startPoints.Count == 0)
                        {
                            failedCount++;
                            roomResults.Add(new
                            {
                                RoomId = rawRoomId,
                                RoomName = room.Name,
                                Success = false,
                                Error = "無法取得房間外圈頂點或內縮點"
                            });
                            continue;
                        }

                        List<XYZ> exitPointsOnPlan = exitTargets
                            .Select(t => new XYZ(t.Point.X, t.Point.Y, planElevation))
                            .ToList();

                        using (Transaction trans = new Transaction(doc, $"避難路徑檢核 - {room.Name}"))
                        {
                            trans.Start();
                            var fOpts = trans.GetFailureHandlingOptions();
                            fOpts.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                            fOpts.SetClearAfterRollback(true);
                            fOpts.SetForcedModalHandling(false);
                            trans.SetFailureHandlingOptions(fOpts);

                            IList<PathOfTravel> createdPaths = PathOfTravel.CreateMapped(viewPlan, startPoints, exitPointsOnPlan);

                            var interiorCandidates = new List<CandidateEgressPath>();
                            if (createdPaths != null)
                            {
                                foreach (PathOfTravel p in createdPaths)
                                {
                                    if (p == null || !p.IsValidObject)
                                        continue;

                                    double lengthFeet = GetPathOfTravelLengthFeet(p);
                                    if (lengthFeet <= 1e-6)
                                        continue;

                                    // 步驟 3：檢核路徑是否全程位於建築物室內空間（排除任何穿越室外空地／中庭之路徑）
                                    if (!IsPathStrictlyInterior(doc, p, planElevation, levelDoorCenters))
                                        continue;

                                    int startIdx = FindClosestPointIndex(p.PathStart, startPoints);
                                    int exitIdx = FindClosestPointIndex(p.PathEnd, exitPointsOnPlan);

                                    interiorCandidates.Add(new CandidateEgressPath
                                    {
                                        Path = p,
                                        StartIndex = startIdx,
                                        ExitIndex = exitIdx,
                                        LengthFeet = lengthFeet
                                    });
                                }
                            }

                            // 多出口比選演算法（每個空間僅擇定 1 個最近出口）：
                            // (1) 依候選出口 (ExitIndex) 分組，計算「房內最遠角落 → 該出口」的室內路徑長度 L(R, E_k)
                            //     （同一頂點對同一出口若有重複路徑取最短；再取各頂點中的最大值作為該出口之代表距離）
                            // (2) 優先採用「可由最多房間角落以室內路徑到達」的出口，避免部分角落無法到達而低估距離
                            // (3) 於上述出口中選取 L(R, E_k) 最短者作為該空間的唯一最近出口 E*，僅保留其最不利路徑
                            List<ExitEvaluation> exitEvaluations = interiorCandidates
                                .GroupBy(c => c.ExitIndex)
                                .Select(g =>
                                {
                                    List<CandidateEgressPath> perVertexBest = g
                                        .GroupBy(c => c.StartIndex)
                                        .Select(vg => vg.OrderBy(c => c.LengthFeet).First())
                                        .ToList();
                                    return new ExitEvaluation
                                    {
                                        ExitIndex = g.Key,
                                        CoveredVertexCount = perVertexBest.Count,
                                        WorstPath = perVertexBest.OrderByDescending(c => c.LengthFeet).First()
                                    };
                                })
                                .ToList();

                            int maxCoverage = exitEvaluations.Count > 0
                                ? exitEvaluations.Max(e => e.CoveredVertexCount)
                                : 0;

                            ExitEvaluation? nearestExitEval = exitEvaluations
                                .Where(e => e.CoveredVertexCount == maxCoverage)
                                .OrderBy(e => e.WorstPath.LengthFeet)
                                .FirstOrDefault();

                            CandidateEgressPath? worstBestPath = nearestExitEval?.WorstPath;

                            var candidateExitSummaries = exitEvaluations
                                .OrderBy(e => e.WorstPath.LengthFeet)
                                .Select(e => new
                                {
                                    DoorId = exitTargets[e.ExitIndex].DoorId,
                                    Label = exitTargets[e.ExitIndex].Label,
                                    FarthestDistanceMeters = Math.Round(
                                        UnitUtils.ConvertFromInternalUnits(e.WorstPath.LengthFeet, UnitTypeId.Meters), 2),
                                    CoveredVertices = e.CoveredVertexCount,
                                    TotalVertices = startPoints.Count,
                                    IsNearest = nearestExitEval != null && e.ExitIndex == nearestExitEval.ExitIndex
                                })
                                .ToList();

                            // 其餘路徑全數 doc.Delete()
                            if (createdPaths != null)
                            {
                                ElementId keepId = worstBestPath?.Path.Id ?? ElementId.InvalidElementId;
                                foreach (PathOfTravel p in createdPaths)
                                {
                                    if (p != null && p.IsValidObject && p.Id != keepId)
                                    {
                                        doc.Delete(p.Id);
                                    }
                                }
                            }

                            if (worstBestPath == null)
                            {
                                trans.Commit();
                                failedCount++;
                                roomResults.Add(new
                                {
                                    RoomId = rawRoomId,
                                    RoomName = room.Name,
                                    RoomNumber = room.Number,
                                    Success = false,
                                    Error = "PathOfTravel.CreateMapped 未能計算出任何全程位於室內的逃生路徑"
                                });
                                continue;
                            }

                            double actualDistFeet = worstBestPath.LengthFeet;
                            double actualDistMeters = Math.Round(
                                UnitUtils.ConvertFromInternalUnits(actualDistFeet, UnitTypeId.Meters), 2);
                            bool isCompliant = actualDistFeet <= maxAllowedFeet + 1e-6;
                            string complianceStatus = isCompliant ? "合格" : "不合格";
                            ExitDoorTarget chosenExit = exitTargets[worstBestPath.ExitIndex];

                            // 4. 寫入房間的 4 個 GUID 共用參數（長度以 Internal Feet 寫入）
                            SetRoomParameter(room, GuidActualDist, actualDistFeet);
                            SetRoomParameter(room, GuidMaxAllowed, maxAllowedFeet);
                            SetRoomParameter(room, GuidIsCompliant, complianceStatus);
                            SetRoomParameter(room, GuidTargetExit, chosenExit.Label);

                            // 5. 使用 View.SetElementOverrides 上色並嘗試建立 IndependentTag
                            OverrideGraphicSettings ogs = new OverrideGraphicSettings();
                            if (isCompliant)
                            {
                                ogs.SetProjectionLineColor(new Color(0, 170, 60));
                                ogs.SetProjectionLineWeight(5);
                            }
                            else
                            {
                                ogs.SetProjectionLineColor(new Color(220, 20, 20));
                                ogs.SetProjectionLineWeight(7);
                                if (dashPatternId != ElementId.InvalidElementId)
                                {
                                    ogs.SetProjectionLinePatternId(dashPatternId);
                                }
                            }
                            viewPlan.SetElementOverrides(worstBestPath.Path.Id, ogs);

                            ElementId? createdTagId = TryCreatePathMidpointTag(doc, viewPlan, worstBestPath.Path);

                            // 6. 針對逃生路徑每個轉折線段（>= 10cm）建立 Revit 原生線性尺寸標註
                            var (segmentLengthsMeters, segmentDimensionIds) = CreatePathSegmentDimensions(
                                doc,
                                viewPlan,
                                worstBestPath.Path,
                                planElevation,
                                minSegmentFeet,
                                dimensionSegments,
                                isCompliant);

                            trans.Commit();

                            if (isCompliant)
                                compliantCount++;
                            else
                                nonCompliantCount++;

                            assignedRoomCounts[chosenExit.DoorId] = assignedRoomCounts.TryGetValue(chosenExit.DoorId, out int prevCount)
                                ? prevCount + 1
                                : 1;

                            roomResults.Add(new
                            {
                                RoomId = rawRoomId,
                                RoomName = room.Name,
                                RoomNumber = room.Number,
                                Success = true,
                                PathOfTravelId = worstBestPath.Path.Id.GetIdValue(),
                                TagId = createdTagId?.GetIdValue(),
                                ActualDistanceMeters = actualDistMeters,
                                MaxAllowedMeters = maxAllowedMeters,
                                SegmentLengthsMeters = segmentLengthsMeters,
                                SegmentDimensionIds = segmentDimensionIds,
                                IsCompliant = complianceStatus,
                                TargetExit = chosenExit.Label,
                                TargetExitDoorId = chosenExit.DoorId,
                                NearestExitAutoSelected = exitTargets.Count > 1,
                                CandidateExits = candidateExitSummaries
                            });
                        }
                    }
                }
                finally
                {
                    // 移除尋路期間建立的非目標外門臨時阻斷牆，確保原建築模型不留任何臨時幾何
                    if (tempBarrierWallIds.Count > 0)
                    {
                        using (Transaction cleanupTrans = new Transaction(doc, "移除臨時外門阻斷牆"))
                        {
                            cleanupTrans.Start();
                            var fOpts = cleanupTrans.GetFailureHandlingOptions();
                            fOpts.SetFailuresPreprocessor(new SilentFailuresPreprocessor());
                            fOpts.SetClearAfterRollback(true);
                            fOpts.SetForcedModalHandling(false);
                            cleanupTrans.SetFailureHandlingOptions(fOpts);

                            foreach (ElementId wid in tempBarrierWallIds)
                            {
                                try
                                {
                                    if (doc.GetElement(wid) != null)
                                    {
                                        doc.Delete(wid);
                                    }
                                }
                                catch
                                {
                                    // 忽略單一臨時牆刪除失敗
                                }
                            }
                            cleanupTrans.Commit();
                        }
                    }
                    // 隨距離檢討自動於 Revit 專案中建立或更新「避難步行距離檢討表」明細表（ViewSchedule），但不直接排版到圖紙上
                    using (Transaction schedTrans = new Transaction(doc, "建立或更新避難步行距離檢討明細表"))
                    {
                        schedTrans.Start();
                        ViewSchedule egressSchedule = EgressSheetNoteService.EnsureOrUpdateEgressSchedule(
                            doc,
                            out resolvedScheduleFields,
                            removeSheetInstances: true);
                        resolvedScheduleId = egressSchedule.Id.GetIdValue();
                        resolvedScheduleName = egressSchedule.Name;
                        schedTrans.Commit();
                    }
                }

                tg.Assimilate();
            }

            return new
            {
                TotalRooms = roomIds.Count,
                CompliantCount = compliantCount,
                NonCompliantCount = nonCompliantCount,
                FailedCount = failedCount,
                MaxAllowedMeters = maxAllowedMeters,
                OccupancyCategory = resolvedOccupancyCategory,
                LevelName = resolvedLevelName,
                LevelElevationMeters = resolvedLevelElevationMeters,
                ViewId = resolvedViewId,
                ViewName = resolvedViewName,
                ScheduleId = resolvedScheduleId,
                ScheduleName = resolvedScheduleName,
                ScheduleFields = resolvedScheduleFields,
                SchedulePlacedOnSheet = false,
                ExitDoors = exitTargets.Select(t => new
                {
                    DoorId = t.DoorId,
                    Label = t.Label,
                    AssignedRoomCount = assignedRoomCounts.TryGetValue(t.DoorId, out int cnt) ? cnt : 0
                }).ToList(),
                MultiExitNearestSelection = exitTargets.Count > 1,
                Results = roomResults
            };
        }

        /// <summary>
        /// 取得房間外圈邊界頂點，並向房間中心（或 8 方位放射搭配 room.IsPointInRoom）內縮指定距離。
        /// </summary>
        private static List<XYZ> GetInsetRoomBoundaryVertices(Room room, double insetFeet, double planElevation)
        {
            var result = new List<XYZ>();
            var boundaryOptions = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };

            IList<IList<BoundarySegment>>? loops = room.GetBoundarySegments(boundaryOptions);
            if (loops == null || loops.Count == 0)
            {
                if (room.Location is LocationPoint lp && room.IsPointInRoom(lp.Point))
                {
                    result.Add(new XYZ(lp.Point.X, lp.Point.Y, planElevation));
                }
                return result;
            }

            // 取周長最長的迴圈作為房間外圈邊界
            IList<BoundarySegment> outerLoop = loops
                .OrderByDescending(loop => loop.Sum(seg => seg.GetCurve()?.Length ?? 0.0))
                .First();

            var rawVertices = new List<XYZ>();
            foreach (BoundarySegment seg in outerLoop)
            {
                Curve? curve = seg.GetCurve();
                if (curve == null)
                    continue;

                XYZ pt = curve.GetEndPoint(0);
                if (rawVertices.Count == 0 || rawVertices.Last().DistanceTo(pt) > 1e-3)
                {
                    rawVertices.Add(pt);
                }
            }

            if (rawVertices.Count == 0)
                return result;

            // 計算房間中心點（以頂點幾何中心為主，若需要也可參考 LocationPoint）
            double avgX = rawVertices.Average(v => v.X);
            double avgY = rawVertices.Average(v => v.Y);
            double testZ = rawVertices[0].Z;
            XYZ roomCenter = new XYZ(avgX, avgY, testZ);

            foreach (XYZ vertex in rawVertices)
            {
                XYZ? validInsetPoint = null;

                // 策略 A：直接往房間中心方向內縮 0.3m
                XYZ toCenter = new XYZ(roomCenter.X - vertex.X, roomCenter.Y - vertex.Y, 0);
                if (toCenter.GetLength() > 1e-6)
                {
                    XYZ dirToCenter = toCenter.Normalize();
                    XYZ candidate = new XYZ(
                        vertex.X + dirToCenter.X * insetFeet,
                        vertex.Y + dirToCenter.Y * insetFeet,
                        testZ);

                    if (room.IsPointInRoom(candidate))
                    {
                        validInsetPoint = new XYZ(candidate.X, candidate.Y, planElevation);
                    }
                }

                // 策略 B：若往中心內縮點不在房間內（例如 L 型或凹多邊形房間），改用 8 方位放射搭配 room.IsPointInRoom
                if (validInsetPoint == null)
                {
                    XYZ preferredDir = toCenter.GetLength() > 1e-6 ? toCenter.Normalize() : XYZ.BasisX;
                    foreach (XYZ dir in EightDirections.OrderByDescending(d => d.DotProduct(preferredDir)))
                    {
                        XYZ candidate = new XYZ(
                            vertex.X + dir.X * insetFeet,
                            vertex.Y + dir.Y * insetFeet,
                            testZ);

                        if (room.IsPointInRoom(candidate))
                        {
                            validInsetPoint = new XYZ(candidate.X, candidate.Y, planElevation);
                            break;
                        }
                    }
                }

                if (validInsetPoint != null &&
                    !result.Any(existing => existing.DistanceTo(validInsetPoint) < 1e-2))
                {
                    result.Add(validInsetPoint);
                }
            }

            // 保底：若所有頂點內縮後皆不在房間內，使用房間 LocationPoint
            if (result.Count == 0 && room.Location is LocationPoint locPt)
            {
                result.Add(new XYZ(locPt.Point.X, locPt.Point.Y, planElevation));
            }

            return result;
        }

        private static ViewPlan? ResolveFloorPlanForRoom(Document doc, Room room)
        {
            if (doc.ActiveView is ViewPlan activePlan &&
                !activePlan.IsTemplate &&
                activePlan.ViewType == ViewType.FloorPlan &&
                activePlan.GenLevel != null &&
                activePlan.GenLevel.Id == room.LevelId)
            {
                return activePlan;
            }

            ViewPlan? matchingPlan = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewPlan))
                .Cast<ViewPlan>()
                .FirstOrDefault(vp =>
                    !vp.IsTemplate &&
                    vp.ViewType == ViewType.FloorPlan &&
                    vp.GenLevel != null &&
                    vp.GenLevel.Id == room.LevelId);

            return matchingPlan ?? (doc.ActiveView as ViewPlan);
        }

        private static XYZ? GetElementCenterPoint(Element elem)
        {
            if (elem.Location is LocationPoint lp)
                return lp.Point;

            if (elem.Location is LocationCurve lc && lc.Curve != null)
                return lc.Curve.Evaluate(0.5, true);

            BoundingBoxXYZ? bbox = elem.get_BoundingBox(null);
            if (bbox != null)
                return (bbox.Min + bbox.Max) * 0.5;

            return null;
        }

        /// <summary>
        /// 步驟 1：取得出口門位於「室內空間側」的目標點，避免目標點落在門框外皮或室外側而導致尋路從建築外部接近。
        /// </summary>
        private static XYZ? GetInteriorDoorTargetPoint(Document doc, Element doorElem, double insetFeet)
        {
            XYZ? basePt = GetElementCenterPoint(doorElem);
            if (basePt == null)
                return null;

            double levelZ = basePt.Z;
            if (doorElem.LevelId != ElementId.InvalidElementId && doc.GetElement(doorElem.LevelId) is Level lvl)
            {
                levelZ = lvl.Elevation;
            }
            double testZ = levelZ + 2.0; // 抬升約 60cm 以確保落在 3D Room 體積內

            if (doorElem is FamilyInstance fi)
            {
                XYZ facing = new XYZ(fi.FacingOrientation.X, fi.FacingOrientation.Y, 0);
                if (facing.GetLength() > 1e-6)
                {
                    facing = facing.Normalize();
                    double[] testOffsets = new double[] { insetFeet, insetFeet * 1.5, insetFeet * 2.5 };

                    foreach (double offset in testOffsets)
                    {
                        XYZ plusTest = new XYZ(basePt.X + facing.X * offset, basePt.Y + facing.Y * offset, testZ);
                        XYZ minusTest = new XYZ(basePt.X - facing.X * offset, basePt.Y - facing.Y * offset, testZ);

                        Room? roomPlus = doc.GetRoomAtPoint(plusTest);
                        Room? roomMinus = doc.GetRoomAtPoint(minusTest);

                        if (roomPlus != null && roomMinus == null)
                        {
                            return new XYZ(plusTest.X, plusTest.Y, levelZ);
                        }
                        if (roomMinus != null && roomPlus == null)
                        {
                            return new XYZ(minusTest.X, minusTest.Y, levelZ);
                        }
                        if (roomPlus != null && roomMinus != null)
                        {
                            // 雙側皆為室內空間（室內安全梯門或防火門），確認中心點或微偏點即可
                            Room? centerRoom = doc.GetRoomAtPoint(new XYZ(basePt.X, basePt.Y, testZ));
                            if (centerRoom != null)
                                return new XYZ(basePt.X, basePt.Y, levelZ);
                            return new XYZ(plusTest.X, plusTest.Y, levelZ);
                        }
                    }
                }

                // 備援：以 8 方位尋找位於 Room 內的點
                foreach (XYZ dir in EightDirections)
                {
                    XYZ cand = new XYZ(basePt.X + dir.X * insetFeet, basePt.Y + dir.Y * insetFeet, testZ);
                    if (doc.GetRoomAtPoint(cand) != null)
                    {
                        return new XYZ(cand.X, cand.Y, levelZ);
                    }
                }
            }

            return new XYZ(basePt.X, basePt.Y, levelZ);
        }

        /// <summary>
        /// 清理受檢房間先前留下的舊 PathOfTravel 與轉折線段標註輔助線，避免重複執行時殘留舊路徑。
        /// </summary>
        private static void CleanupPreviousRoomAuditElements(
            Document doc,
            ViewPlan viewPlan,
            IList<Room> targetRooms,
            double planElevation)
        {
            if (targetRooms == null || targetRooms.Count == 0)
                return;

            double testZ = planElevation + 2.0;
            var existingPaths = new FilteredElementCollector(doc, viewPlan.Id)
                .OfClass(typeof(PathOfTravel))
                .Cast<PathOfTravel>()
                .ToList();

            if (existingPaths.Count == 0)
                return;

            var endpointsToClean = new List<XYZ>();
            var pathIdsToDelete = new List<ElementId>();

            foreach (PathOfTravel oldPath in existingPaths)
            {
                if (oldPath == null || !oldPath.IsValidObject)
                    continue;

                XYZ startTest = new XYZ(oldPath.PathStart.X, oldPath.PathStart.Y, testZ);
                bool belongsToTargetRoom = targetRooms.Any(r =>
                {
                    try
                    {
                        return r.IsPointInRoom(startTest);
                    }
                    catch
                    {
                        return false;
                    }
                });

                if (!belongsToTargetRoom)
                    continue;

                IList<Curve>? curves = oldPath.GetCurves();
                if (curves != null)
                {
                    foreach (Curve c in curves)
                    {
                        if (c == null)
                            continue;
                        endpointsToClean.Add(new XYZ(c.GetEndPoint(0).X, c.GetEndPoint(0).Y, planElevation));
                        endpointsToClean.Add(new XYZ(c.GetEndPoint(1).X, c.GetEndPoint(1).Y, planElevation));
                    }
                }

                pathIdsToDelete.Add(oldPath.Id);
            }

            if (endpointsToClean.Count > 0)
            {
                // 先清理舊路徑上的 Dimension 尺寸標註（避免刪除 DetailCurve 時拋出尺寸參照遺失的 Modal 對話框）
                var existingDimensions = new FilteredElementCollector(doc, viewPlan.Id)
                    .OfClass(typeof(Dimension))
                    .Cast<Dimension>()
                    .ToList();

                foreach (Dimension dim in existingDimensions)
                {
                    try
                    {
                        if (dim.Curve != null)
                        {
                            XYZ mid = dim.Curve.Evaluate(0.5, true);
                            XYZ midPlanar = new XYZ(mid.X, mid.Y, planElevation);
                            if (endpointsToClean.Any(ep => ep.DistanceTo(midPlanar) < 2.0))
                            {
                                doc.Delete(dim.Id);
                            }
                        }
                    }
                    catch
                    {
                    }
                }

                var detailCurves = new FilteredElementCollector(doc, viewPlan.Id)
                    .OfClass(typeof(CurveElement))
                    .OfCategory(BuiltInCategory.OST_Lines)
                    .Cast<CurveElement>()
                    .ToList();

                foreach (CurveElement ce in detailCurves)
                {
                    try
                    {
                        Curve? gc = ce.GeometryCurve;
                        if (gc == null)
                            continue;

                        // 僅清理長度約 0.5 ft (tickHalfLen * 2) 且中點位於舊路徑轉折端點處的隱形標註參考短線
                        if (Math.Abs(gc.Length - 0.5) < 0.08)
                        {
                            XYZ mid = gc.Evaluate(0.5, true);
                            XYZ midPlanar = new XYZ(mid.X, mid.Y, planElevation);
                            if (endpointsToClean.Any(ep => ep.DistanceTo(midPlanar) < 0.15))
                            {
                                doc.Delete(ce.Id);
                            }
                        }
                    }
                    catch
                    {
                        // 忽略單一輔助線清理失敗
                    }
                }
            }

            foreach (ElementId pid in pathIdsToDelete)
            {
                try
                {
                    if (doc.GetElement(pid) != null)
                    {
                        doc.Delete(pid);
                    }
                }
                catch
                {
                    // 忽略單一舊路徑刪除失敗
                }
            }
        }

        /// <summary>
        /// 步驟 2：識別樓層中所有「非目標出口之建築外門」，並於其室外側建立臨時 U 型阻斷牆，強制 PathOfTravel 只能走室內走廊與內門。
        /// </summary>
        private static List<ElementId> CreateTemporaryExteriorDoorBarriers(
            Document doc,
            ViewPlan viewPlan,
            HashSet<long> targetExitDoorIds,
            double planElevation,
            out List<XYZ> validInteriorAndTargetDoorCenters)
        {
            var barrierWallIds = new List<ElementId>();
            validInteriorAndTargetDoorCenters = new List<XYZ>();

            ElementId levelId = viewPlan.GenLevel?.Id ?? ElementId.InvalidElementId;
            if (levelId == ElementId.InvalidElementId)
                return barrierWallIds;

            WallType? basicWallType = new FilteredElementCollector(doc)
                .OfClass(typeof(WallType))
                .Cast<WallType>()
                .FirstOrDefault(wt => wt.Kind == WallKind.Basic);

            if (basicWallType == null)
                return barrierWallIds;

            double testZ = planElevation + 2.0;

            var doors = new FilteredElementCollector(doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .OfClass(typeof(FamilyInstance))
                .Cast<FamilyInstance>()
                .ToList();

            foreach (FamilyInstance door in doors)
            {
                if (door == null || !door.IsValidObject)
                    continue;

                // 確認該門位於當前檢討樓層高度附近
                XYZ? doorPt = GetElementCenterPoint(door);
                if (doorPt == null)
                    continue;

                if (door.LevelId != ElementId.InvalidElementId && door.LevelId != levelId)
                {
                    if (Math.Abs(doorPt.Z - planElevation) > 6.0)
                        continue;
                }

                long doorId = door.Id.GetIdValue();
                XYZ centerOnPlan = new XYZ(doorPt.X, doorPt.Y, planElevation);

                XYZ facing = new XYZ(door.FacingOrientation.X, door.FacingOrientation.Y, 0);
                facing = facing.GetLength() > 1e-6 ? facing.Normalize() : XYZ.BasisY;

                XYZ hand = new XYZ(door.HandOrientation.X, door.HandOrientation.Y, 0);
                hand = hand.GetLength() > 1e-6 ? hand.Normalize() : new XYZ(-facing.Y, facing.X, 0);

                // 檢查門前後兩側（約 0.6m 與 1.05m 處）是否皆有 Room
                XYZ plusNear = new XYZ(centerOnPlan.X + facing.X * 2.0, centerOnPlan.Y + facing.Y * 2.0, testZ);
                XYZ plusFar = new XYZ(centerOnPlan.X + facing.X * 3.5, centerOnPlan.Y + facing.Y * 3.5, testZ);
                XYZ minusNear = new XYZ(centerOnPlan.X - facing.X * 2.0, centerOnPlan.Y - facing.Y * 2.0, testZ);
                XYZ minusFar = new XYZ(centerOnPlan.X - facing.X * 3.5, centerOnPlan.Y - facing.Y * 3.5, testZ);

                bool hasRoomPlus = doc.GetRoomAtPoint(plusNear) != null || doc.GetRoomAtPoint(plusFar) != null;
                bool hasRoomMinus = doc.GetRoomAtPoint(minusNear) != null || doc.GetRoomAtPoint(minusFar) != null;

                bool isExteriorDoor = !hasRoomPlus || !hasRoomMinus;

                BoundingBoxXYZ? doorBbox = door.get_BoundingBox(null);
                double bboxSpan = doorBbox != null
                    ? Math.Max(Math.Abs(doorBbox.Max.X - doorBbox.Min.X), Math.Abs(doorBbox.Max.Y - doorBbox.Min.Y))
                    : 6.0;
                double dynamicHalfWidth = Math.Max(4.5, bboxSpan * 0.5 + 1.5);

                if (targetExitDoorIds.Contains(doorId))
                {
                    // 目標逃生出口門：允許通行，但若是外門，則於其「室外側」外推處加上 U 型防堵罩，防止路徑從建築物外部繞入此出口門
                    validInteriorAndTargetDoorCenters.Add(centerOnPlan);

                    if (isExteriorDoor && (hasRoomPlus ^ hasRoomMinus))
                    {
                        XYZ extDir = !hasRoomPlus ? facing : -facing;
                        CreateExteriorDoorUCapBarrier(
                            doc,
                            basicWallType.Id,
                            levelId,
                            centerOnPlan,
                            hand,
                            extDir,
                            dynamicHalfWidth,
                            1.8, // 往室外側外推 1.8 ft (~55cm)
                            barrierWallIds);
                    }
                    continue;
                }

                if (!isExteriorDoor)
                {
                    // 兩側皆為室內空間的室內門：正常保留通行並記錄門中心點
                    validInteriorAndTargetDoorCenters.Add(centerOnPlan);
                    continue;
                }

                // 非目標之建築外門：於門扇開口處與室外側建立臨時阻斷牆，封鎖穿出建築物外部的捷徑
                XYZ exteriorDir = !hasRoomPlus ? facing : (!hasRoomMinus ? -facing : facing);
                CreateExteriorDoorUCapBarrier(
                    doc,
                    basicWallType.Id,
                    levelId,
                    centerOnPlan,
                    hand,
                    exteriorDir,
                    dynamicHalfWidth,
                    1.0, // 往室外側外推 1.0 ft (~30cm)
                    barrierWallIds);
            }

            return barrierWallIds;
        }

        private static void CreateExteriorDoorUCapBarrier(
            Document doc,
            ElementId wallTypeId,
            ElementId levelId,
            XYZ doorCenter,
            XYZ handDir,
            XYZ extDir,
            double halfWidthFeet,
            double extDepthFeet,
            List<ElementId> createdWallIds)
        {
            try
            {
                // 側翼起點位於門外側 0.2 ft (~6cm)，完全不碰觸或嵌入原外牆／帷幕牆
                XYZ pLeftWall = doorCenter - handDir * halfWidthFeet + extDir * 0.2;
                XYZ pLeftExt = doorCenter - handDir * halfWidthFeet + extDir * extDepthFeet;
                XYZ pRightWall = doorCenter + handDir * halfWidthFeet + extDir * 0.2;
                XYZ pRightExt = doorCenter + handDir * halfWidthFeet + extDir * extDepthFeet;

                Line[] barrierLines = new Line[]
                {
                    Line.CreateBound(pLeftExt, pRightExt),
                    Line.CreateBound(pLeftWall, pLeftExt),
                    Line.CreateBound(pRightWall, pRightExt)
                };

                foreach (Line line in barrierLines)
                {
                    if (line.Length <= 0.1)
                        continue;

                    Wall w = Wall.Create(doc, line, wallTypeId, levelId, 10.0, 0.0, false, false);
                    if (w != null)
                    {
                        WallUtils.DisallowWallJoinAtEnd(w, 0);
                        WallUtils.DisallowWallJoinAtEnd(w, 1);
                        createdWallIds.Add(w.Id);
                    }
                }
            }
            catch
            {
                // 忽略單一臨時阻斷牆建立例外
            }
        }

        /// <summary>
        /// 步驟 3：檢核 PathOfTravel 的所有轉折線段是否全程位於建築物室內空間（Room 涵蓋範圍或室內門過渡區）。
        /// </summary>
        private static bool IsPathStrictlyInterior(
            Document doc,
            PathOfTravel path,
            double planElevation,
            IList<XYZ> validDoorCenters)
        {
            IList<Curve>? curves = path.GetCurves();
            if (curves == null || curves.Count == 0)
                return false;

            double testZ = planElevation + 2.0;
            double sampleStepFeet = 1.5; // 每隔約 45cm 取樣檢查一次

            foreach (Curve curve in curves)
            {
                if (curve == null || curve.Length <= 1e-4)
                    continue;

                double len = curve.Length;
                int steps = Math.Max(2, (int)Math.Ceiling(len / sampleStepFeet));

                for (int i = 0; i <= steps; i++)
                {
                    double param = (double)i / steps;
                    XYZ pt = curve.Evaluate(param, true);
                    if (!IsPointInAnyRoomOrDoorThreshold(doc, pt, testZ, validDoorCenters))
                    {
                        return false;
                    }
                }
            }

            return true;
        }

        private static bool IsPointInAnyRoomOrDoorThreshold(
            Document doc,
            XYZ pt,
            double testZ,
            IList<XYZ> validDoorCenters)
        {
            XYZ testPt = new XYZ(pt.X, pt.Y, testZ);
            if (doc.GetRoomAtPoint(testPt) != null)
                return true;

            // 若取樣點剛好落在室內門框或隔間牆厚度過渡區（約 35cm = 1.15 ft 內），檢查其四向微幅偏移是否位於 Room 內
            double probeOffset = 1.15;
            foreach (XYZ dir in EightDirections)
            {
                XYZ probe = new XYZ(pt.X + dir.X * probeOffset, pt.Y + dir.Y * probeOffset, testZ);
                if (doc.GetRoomAtPoint(probe) != null)
                {
                    // 確認反方向或鄰近點也在室內（或是位於有效門中心點 3.0 ft 內），避免將外牆外側 35cm 誤判為室內
                    XYZ oppositeProbe = new XYZ(pt.X - dir.X * probeOffset, pt.Y - dir.Y * probeOffset, testZ);
                    if (doc.GetRoomAtPoint(oppositeProbe) != null)
                        return true;
                }
            }

            // 檢查是否位於有效室內門或目標出口門中心 3.0 ft (~0.9m) 門檻過渡範圍內
            if (validDoorCenters != null)
            {
                foreach (XYZ doorCenter in validDoorCenters)
                {
                    double dist2D = Math.Sqrt(
                        Math.Pow(pt.X - doorCenter.X, 2) +
                        Math.Pow(pt.Y - doorCenter.Y, 2));
                    if (dist2D <= 3.0)
                        return true;
                }
            }

            return false;
        }

        private static double GetPathOfTravelLengthFeet(PathOfTravel path)
        {
            IList<Curve>? curves = path.GetCurves();
            if (curves != null && curves.Count > 0)
            {
                double curveLengthSum = curves.Sum(c => c?.Length ?? 0.0);
                if (curveLengthSum > 1e-6)
                    return curveLengthSum;
            }

            Parameter? lengthParam = path.LookupParameter("Length") ?? path.LookupParameter("長度");
            if (lengthParam != null && lengthParam.HasValue && lengthParam.StorageType == StorageType.Double)
            {
                return lengthParam.AsDouble();
            }

            return 0.0;
        }

        private static int FindClosestPointIndex(XYZ target, IList<XYZ> points)
        {
            int bestIndex = 0;
            double minDist = double.MaxValue;

            for (int i = 0; i < points.Count; i++)
            {
                double d = Math.Sqrt(
                    Math.Pow(target.X - points[i].X, 2) +
                    Math.Pow(target.Y - points[i].Y, 2));
                if (d < minDist)
                {
                    minDist = d;
                    bestIndex = i;
                }
            }

            return bestIndex;
        }

        private static void SetRoomParameter(Room room, Guid paramGuid, double value)
        {
            Parameter? param = room.get_Parameter(paramGuid);
            if (param != null && !param.IsReadOnly)
            {
                param.Set(value);
            }
        }

        private static void SetRoomParameter(Room room, Guid paramGuid, string value)
        {
            Parameter? param = room.get_Parameter(paramGuid);
            if (param != null && !param.IsReadOnly)
            {
                param.Set(value ?? string.Empty);
            }
        }

        private static ElementId FindDashLinePatternId(Document doc)
        {
            LinePatternElement? dashPattern = new FilteredElementCollector(doc)
                .OfClass(typeof(LinePatternElement))
                .Cast<LinePatternElement>()
                .FirstOrDefault(lp =>
                    lp.Name.Equals("Dash", StringComparison.OrdinalIgnoreCase) ||
                    lp.Name.IndexOf("Dash", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    lp.Name.Contains("虛線"));

            return dashPattern?.Id ?? ElementId.InvalidElementId;
        }

        private static ElementId? TryCreatePathMidpointTag(Document doc, ViewPlan viewPlan, PathOfTravel path)
        {
            try
            {
                XYZ midPoint = GetPathMidpoint(path);
                IndependentTag? tag = IndependentTag.Create(
                    doc,
                    viewPlan.Id,
                    new Reference(path),
                    false,
                    TagMode.TM_ADDBY_CATEGORY,
                    TagOrientation.Horizontal,
                    midPoint);

                return tag?.Id;
            }
            catch
            {
                // 若專案尚未載入移動路徑標籤族群，忽略標籤建立失敗，不中斷整體檢核流程
                return null;
            }
        }

        private static XYZ GetPathMidpoint(PathOfTravel path)
        {
            IList<Curve>? curves = path.GetCurves();
            if (curves == null || curves.Count == 0)
            {
                return (path.PathStart + path.PathEnd) * 0.5;
            }

            double totalLength = curves.Sum(c => c.Length);
            double halfLength = totalLength * 0.5;
            double accumulated = 0.0;

            foreach (Curve curve in curves)
            {
                if (accumulated + curve.Length >= halfLength && curve.Length > 1e-6)
                {
                    double normalizedParam = (halfLength - accumulated) / curve.Length;
                    normalizedParam = Math.Max(0.0, Math.Min(1.0, normalizedParam));
                    return curve.Evaluate(normalizedParam, true);
                }
                accumulated += curve.Length;
            }

            return curves[curves.Count / 2].Evaluate(0.5, true);
        }

        /// <summary>
        /// 針對逃生路徑的每個轉折線段提取長度，並對長度 >= minSegmentFeet (預設 10cm) 的線段建立 Revit 原生線性尺寸標註 (Dimension)。
        /// </summary>
        private static (List<double> SegmentLengthsMeters, List<long> SegmentDimensionIds) CreatePathSegmentDimensions(
            Document doc,
            ViewPlan viewPlan,
            PathOfTravel path,
            double planElevation,
            double minSegmentFeet,
            bool dimensionSegments,
            bool isCompliant)
        {
            var segmentLengthsMeters = new List<double>();
            var segmentDimensionIds = new List<long>();

            IList<Curve>? curves = path.GetCurves();
            if (curves == null || curves.Count == 0)
            {
                return (segmentLengthsMeters, segmentDimensionIds);
            }

            GraphicsStyle? invisibleStyle = dimensionSegments ? TryFindInvisibleLineStyle(doc) : null;
            OverrideGraphicSettings dimOgs = new OverrideGraphicSettings();
            dimOgs.SetProjectionLineColor(isCompliant ? new Color(0, 170, 60) : new Color(220, 20, 20));

            double tickHalfLen = 0.25; // 垂直隱形參考短線半長（約 75mm）

            foreach (Curve curve in curves)
            {
                if (curve == null || curve.Length <= 1e-6)
                    continue;

                double segMeters = Math.Round(
                    UnitUtils.ConvertFromInternalUnits(curve.Length, UnitTypeId.Meters), 2);
                segmentLengthsMeters.Add(segMeters);

                // 過濾小於 10cm (minSegmentFeet) 的微小轉折碎線段，或未啟用分段標註時跳過
                if (!dimensionSegments || curve.Length < minSegmentFeet)
                    continue;

                try
                {
                    XYZ p0 = curve.GetEndPoint(0);
                    XYZ p1 = curve.GetEndPoint(1);
                    XYZ start = new XYZ(p0.X, p0.Y, planElevation);
                    XYZ end = new XYZ(p1.X, p1.Y, planElevation);

                    double planarLength = start.DistanceTo(end);
                    if (planarLength < minSegmentFeet)
                        continue;

                    XYZ dir = (end - start).Normalize();
                    XYZ perp = new XYZ(-dir.Y, dir.X, 0);
                    if (perp.GetLength() <= 1e-6)
                    {
                        perp = XYZ.BasisY;
                    }
                    else
                    {
                        perp = perp.Normalize();
                    }

                    // 於轉折線段兩端建立垂直於線段方向的微短參考 DetailCurve（套用 <Invisible lines> 隱藏線樣式）
                    DetailCurve dc1 = doc.Create.NewDetailCurve(
                        viewPlan,
                        Line.CreateBound(start - perp * tickHalfLen, start + perp * tickHalfLen));
                    DetailCurve dc2 = doc.Create.NewDetailCurve(
                        viewPlan,
                        Line.CreateBound(end - perp * tickHalfLen, end + perp * tickHalfLen));

                    if (invisibleStyle != null)
                    {
                        try
                        {
                            dc1.LineStyle = invisibleStyle;
                            dc2.LineStyle = invisibleStyle;
                        }
                        catch
                        {
                            // 若線樣式套用失敗則忽略
                        }
                    }

                    ReferenceArray refArray = new ReferenceArray();
                    refArray.Append(dc1.GeometryCurve.Reference);
                    refArray.Append(dc2.GeometryCurve.Reference);

                    // 尺寸線直接建立於逃生路徑轉折線段上
                    Line dimLine = Line.CreateBound(start, end);
                    Dimension? dim = doc.Create.NewDimension(viewPlan, dimLine, refArray);
                    if (dim != null)
                    {
                        viewPlan.SetElementOverrides(dim.Id, dimOgs);
                        segmentDimensionIds.Add(dim.Id.GetIdValue());
                    }
                }
                catch
                {
                    // 單一線段標註失敗不中斷整體檢核與其餘線段標註
                }
            }

            return (segmentLengthsMeters, segmentDimensionIds);
        }

        private static GraphicsStyle? TryFindInvisibleLineStyle(Document doc)
        {
            try
            {
                Category? invisibleCat = Category.GetCategory(doc, BuiltInCategory.OST_InvisibleLines);
                GraphicsStyle? style = invisibleCat?.GetGraphicsStyle(GraphicsStyleType.Projection);
                if (style != null)
                    return style;

                Category? linesCat = doc.Settings.Categories.get_Item(BuiltInCategory.OST_Lines);
                if (linesCat != null)
                {
                    foreach (Category subCat in linesCat.SubCategories)
                    {
                        string name = subCat.Name ?? string.Empty;
                        if (name.IndexOf("Invisible", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("不可見", StringComparison.OrdinalIgnoreCase) >= 0 ||
                            name.IndexOf("隱藏", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            return subCat.GetGraphicsStyle(GraphicsStyleType.Projection);
                        }
                    }
                }
            }
            catch
            {
                // 忽略查詢例外
            }

            return null;
        }
    }
}
