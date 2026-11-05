using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;

namespace Group3.EgressExporter
{
    // 從 Revit 梯段路徑找這座樓梯在本層的第一階，所有模型都用同一套規則：
    // 從本層往上 → 最低梯段路徑的起點；到本層為止 → 最高梯段路徑的終點；
    // 穿過本層 → 找本層高度的梯段，地上層優先往下、地下層優先往上。找不到回傳 null，不用樓梯中心代替。
    internal static class StairEndpoint
    {
        internal static XYZ Detect(Document doc, Stairs stair, Level level, out string source, out double diff)
        {
            source = null; diff = 0;
            // 樓梯常有幾公分的頂部／基準偏移（例如面材厚度），所以放到 0.2 m；幾何設定，非法規值
            double tol = UnitUtils.ConvertToInternalUnits(0.2, UnitTypeId.Meters), e = level.ProjectElevation;
            var runs = stair.GetStairsRuns().Select(id => doc.GetElement(id)).OfType<StairsRun>().ToList();
            if (!runs.Any()) return null;
            double dBase = stair.BaseElevation - e, dTop = stair.TopElevation - e;
            if (Math.Abs(dBase) <= tol && Math.Abs(dBase) <= Math.Abs(dTop))
            {
                diff = dBase; source = "lowest_run_path_start";
                return Start(runs.OrderBy(x => x.BaseElevation).First());
            }
            if (Math.Abs(dTop) <= tol)
            {
                diff = dTop; source = "highest_run_path_end";
                return End(runs.OrderByDescending(x => x.TopElevation).First());
            }
            // 梯段高程是相對樓梯底部（Revit API 文件）
            var down = runs.FirstOrDefault(r => Math.Abs(stair.BaseElevation + r.TopElevation - e) <= tol);
            var up = runs.FirstOrDefault(r => Math.Abs(stair.BaseElevation + r.BaseElevation - e) <= tol);
            bool useDown = e < 0 ? up == null : down != null;
            if (useDown && down != null) { source = "level_run_path_end"; diff = stair.BaseElevation + down.TopElevation - e; return End(down); }
            if (up != null) { source = "level_run_path_start"; diff = stair.BaseElevation + up.BaseElevation - e; return Start(up); }
            return null;
        }
        static XYZ Start(StairsRun run) => run.GetStairsPath().FirstOrDefault()?.GetEndPoint(0);
        static XYZ End(StairsRun run) => run.GetStairsPath().LastOrDefault()?.GetEndPoint(1);
    }
    public sealed class Extractor
    {
        readonly Document doc;
        readonly Level level;
        readonly Phase phase;
        readonly ExportSettings settings;
        readonly Snapshot result = new Snapshot();
        public Extractor(Document doc, Level level, Phase phase, ExportSettings settings)
        { this.doc = doc; this.level = level; this.phase = phase; this.settings = settings; }
        static string Id(Element e) => e.Id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        static double M(double v) => UnitUtils.ConvertFromInternalUnits(v, UnitTypeId.Meters);
        static double[] XY(XYZ p) => new[] { M(p.X), M(p.Y) };
        void Warn(Element e, string code, string message, string severity = "warning") =>
            result.warnings.Add(new WarningData { element_id = e == null ? null : Id(e), code = code, message = message, severity = severity });
        IEnumerable<T> All<T>() where T : Element => new FilteredElementCollector(doc)
            // Room is an API-only subclass; Revit's native class filter accepts SpatialElement.
            .OfClass(typeof(T) == typeof(Room) ? typeof(SpatialElement) : typeof(T))
            .WhereElementIsNotElementType().OfType<T>().OrderBy(e => e.Id.Value);
        bool MainModel(Element e) => e.DesignOption == null;
        bool Exists(Element e)
        {
            var status = e.GetPhaseStatus(phase.Id);
            return status == ElementOnPhaseStatus.Existing || status == ElementOnPhaseStatus.New
                || status == ElementOnPhaseStatus.None;
        }
        void Safe(Element e, Action action)
        {
            try { action(); }
            catch (Exception ex) { Warn(e, "ELEMENT_EXTRACTION_FAILED", e.Name + ": " + ex.Message, "error"); }
        }
        public Snapshot Run()
        {
            ValidateSettings();
            var version = Document.GetDocumentVersion(doc);
            result.project = new ProjectData {
                model_name = doc.Title, model_guid = doc.ProjectInformation.UniqueId,
                revit_version = doc.Application.VersionNumber, phase = phase.Name, phase_id = Id(phase),
                design_option = "主模型（不含設計選項）", level_id = Id(level),
                level = new LevelData { name = level.Name, elevation_m = M(level.ProjectElevation) },
                extracted_at = DateTimeOffset.Now.ToString("o"), has_unsaved_changes = doc.IsModified,
                model_version = version.VersionGUID + "/" + version.NumberOfSaves
            };
            foreach (var r in All<Room>().Where(r => r.LevelId == level.Id &&
                r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phase.Id && MainModel(r)))
                Safe(r, () => ReadRoom(r));
            foreach (var door in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType().OfType<FamilyInstance>().OrderBy(e => e.Id.Value)
                .Where(e => MainModel(e) && Exists(e))) Safe(door, () => ReadDoor(door));
            foreach (var stair in All<Stairs>().Where(e => MainModel(e) && Exists(e)))
                Safe(stair, () => ReadStair(stair));
            foreach (var legacy in new FilteredElementCollector(doc).OfCategory(BuiltInCategory.OST_Stairs).WhereElementIsNotElementType()
                .Where(e => !(e is Stairs) && MainModel(e) && Exists(e) && StairKind.CrossesLevel(e, level)).OrderBy(e => e.Id.Value).ToList())
                Safe(legacy, () => ReadLegacyStair(legacy));
            KeepOnlySelectedStairs();
            foreach (var stair in result.stairs.Where(s => s.entry_point == null).ToList()) {
                result.stairs.Remove(stair); result.unresolved_stairs.Add(stair);
            }
            foreach (var r in result.rooms.Where(r => r.door_ids.Count == 0))
                Warn(doc.GetElement(r.unique_id), "ROOM_NO_DOOR", "沒有找到與此房間關聯的門。", "error");
            foreach (var multi in All<MultistoryStairs>())
                Warn(multi, "MULTISTORY_STAIRS_REVIEW", "多層樓梯的重複實例尚未完整展開；請逐樓層核對樓梯數量與終點。", "error");
            foreach (var corridor in settings.corridors) ReadCorridor(corridor);
            ReadColumns();
            foreach (var link in All<RevitLinkInstance>())
                Warn(link, "LINK_NOT_EXTRACTED", "本版只擷取主文件；連結模型未納入，請先確認房間、門、障礙物來源。", "error");
            if (All<DesignOption>().Any()) Warn(null, "DESIGN_OPTIONS_EXCLUDED", "只擷取主模型；設計選項中的元件未納入。", "error");
            if (!result.rooms.Any()) Warn(null, "NO_ROOMS", "所選樓層／階段找不到已放置房間。未放置房間沒有樓層，另列於清單。", "error");
            // 未放置的房間仍記著原本的樓層；別層留下的不影響這次檢討，不列入
            foreach (var r in All<Room>().Where(r => r.Location == null && MainModel(r) &&
                r.get_Parameter(BuiltInParameter.ROOM_PHASE)?.AsElementId() == phase.Id &&
                (r.LevelId == level.Id || r.LevelId == ElementId.InvalidElementId)))
                Warn(r, "ROOM_NOT_PLACED", "房間未放置，無法歸屬所選樓層。", "error");
            // 有通行空間時，B 的引擎可以直接在房間裡排路線，不一定需要中心線
            if (!result.corridors.Any() && !result.rooms.Any(r => r.is_circulation == true)) Warn(null, "NO_CORRIDOR_CENTERLINE", "未設定走廊中心線；可在設定檔指定曲線，或先確認房間的 is_circulation。");
            result.warnings = result.warnings.OrderBy(w => w.element_id, StringComparer.Ordinal)
                .ThenBy(w => w.code, StringComparer.Ordinal).ThenBy(w => w.message, StringComparer.Ordinal).ToList();
            return result;
        }
        void ValidateSettings()
        {
            PointRules.Normalize(settings);
            bool hasMappings = settings.rooms.Any() || settings.doors.Any() || settings.stairs.Any() || settings.corridors.Any();
            if (hasMappings && (settings.model_guid != doc.ProjectInformation.UniqueId ||
                settings.level_id != Id(level) || settings.phase_id != Id(phase)))
                throw new ArgumentException("設定檔的模型、樓層或階段不符。請先匯出本樓層的設定範本。");
            var refs = settings.rooms.Select(x => x?.unique_id).Concat(settings.stairs.Select(x => x?.unique_id))
                .Concat(settings.doors.Select(x => x?.unique_id)).Concat(settings.corridors.Select(x => x?.room_unique_id)).ToList();
            if (refs.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("設定項目必須有 unique_id。");
            if (settings.rooms.GroupBy(x => x.unique_id).Any(x => x.Count() > 1) ||
                settings.doors.GroupBy(x => x.unique_id).Any(x => x.Count() > 1) ||
                settings.stairs.GroupBy(x => x.unique_id).Any(x => x.Count() > 1) ||
                settings.corridors.GroupBy(x => x.room_unique_id).Any(x => x.Count() > 1))
                throw new ArgumentException("設定檔有重複的元件項目。");
            foreach (var uid in refs) if (doc.GetElement(uid) == null)
                throw new ArgumentException("設定元件不存在，請更新設定檔：" + uid);
        }
        List<double[]> Points(IEnumerable<Curve> curves, bool closed)
        {
            var parts = curves.Select(c => c.Tessellate().Select(XY).ToList()).ToList();
            var chain = Geometry.Chain(parts);
            if (closed && Geometry.Distance(chain[0], chain[chain.Count - 1]) > Geometry.Tolerance)
                throw new ArgumentException("輪廓未閉合，不能自動補線。");
            return closed ? Geometry.Ring(chain) : chain;
        }
        void ReadRoom(Room r)
        {
            var mapping = settings.rooms.SingleOrDefault(x => x.unique_id == r.UniqueId);
            var data = new RoomData { element_id = Id(r), unique_id = r.UniqueId, number = r.Number,
                name = r.get_Parameter(BuiltInParameter.ROOM_NAME)?.AsString() ?? r.Name, level = level.Name,
                area_m2 = r.Area > 0 ? (double?)UnitUtils.ConvertFromInternalUnits(r.Area, UnitTypeId.SquareMeters) : null,
                is_habitable = mapping?.is_habitable, is_circulation = mapping?.is_circulation };
            // 設定檔沒寫的，改讀使用者在 Revit 房間「部門」參數填的分類；不從房間名稱推測
            var department = r.get_Parameter(BuiltInParameter.ROOM_DEPARTMENT)?.AsString()?.Trim();
            if (!string.IsNullOrEmpty(department))
            {
                bool? habitable = null, circulation = null;
                if (department == "居室") { habitable = true; circulation = false; }
                else if (department == "通行") { habitable = false; circulation = true; }
                else if (department == "非居室") { habitable = false; circulation = false; }
                else Warn(r, "ROOM_DEPARTMENT_UNKNOWN", "「部門」參數是「" + department + "」；只認得 居室／通行／非居室，未採用。");
                if (!data.is_habitable.HasValue) data.is_habitable = habitable;
                if (!data.is_circulation.HasValue) data.is_circulation = circulation;
            }
            result.rooms.Add(data); // Retain incomplete rooms in the repair list.
            using (var options = new SpatialElementBoundaryOptions { SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish })
            {
                var loops = r.GetBoundarySegments(options);
                if (loops != null)
                    foreach (var loop in loops) {
                        var curves = loop.Select(s => s.GetCurve()).ToList();
                        if (curves.Any(c => !(c is Line))) Warn(r, "CURVE_TESSELLATED", "曲線邊界依 Revit Tessellate 轉成折線，需確認近似誤差。");
                        data.boundary_loops.Add(Points(curves, true));
                    }
            }
            // 內圈（房間內的柱子、孔洞）交給 B 引擎當障礙物繞開；不相連的多個區域仍留空，阻止錯誤路徑
            var outer = Geometry.OuterLoop(data.boundary_loops, out var holes);
            if (outer != null && outer.Count >= 3 && data.area_m2.HasValue)
            {
                data.boundary = outer; data.boundary_closed = true;
                foreach (var hole in holes)
                    result.obstacles.Add(new ObstacleData { element_id = data.element_id, unique_id = data.unique_id,
                        category = "room_inner_boundary", geometry_source = "room_boundary_inner_loop", footprint = hole });
                if (holes.Any()) Warn(r, "ROOM_INNER_LOOP", "房間內有 " + holes.Count + " 個內圈（柱子或孔洞），已當障礙物繞開；請確認它確實不能通行。");
            }
            else if (data.boundary_loops.Count > 1)
                Warn(r, "ROOM_MULTIPLE_LOOPS", "房間有不相連的區域。原始輪廓保存在 boundary_loops；boundary 留空以阻止錯誤路徑。", "error");
            else Warn(r, "ROOM_NOT_ENCLOSED", "房間未封閉、面積為零或沒有有效邊界。", "error");
            if (!data.is_habitable.HasValue) Warn(r, "ROOM_USE_UNCONFIRMED", "是否居室尚未確認；可在房間「部門」參數填 居室／通行／非居室。");
            if (!data.is_circulation.HasValue) Warn(r, "ROOM_CIRCULATION_UNCONFIRMED", "是否通行空間尚未確認；不會以房間名稱推測，可在「部門」參數填 通行。");
        }
        double? ClearWidth(Element e, out string source)
        {
            source = null;
            if (string.IsNullOrWhiteSpace(settings.clear_width_parameter)) return null;
            foreach (var target in new[] { e, doc.GetElement(e.GetTypeId()) }.Where(x => x != null))
            {
                var ps = target.GetParameters(settings.clear_width_parameter);
                if (ps.Count > 1) { Warn(e, "WIDTH_PARAMETER_AMBIGUOUS", "同名淨寬參數不唯一，未採用。"); return null; }
                if (ps.Count == 0) continue;
                var p = ps[0];
                if (!p.HasValue || p.StorageType != StorageType.Double || p.Definition.GetDataType() != SpecTypeId.Length || p.AsDouble() <= 0)
                { Warn(e, "WIDTH_PARAMETER_INVALID", "淨寬參數必須是有值且大於零的長度參數。"); return null; }
                source = (target.Id == e.Id ? "instance:" : "type:") + settings.clear_width_parameter;
                return M(p.AsDouble());
            }
            return null;
        }
        void ReadDoor(FamilyInstance door)
        {
            var adjacent = new[] { door.get_FromRoom(phase), door.get_ToRoom(phase) }.Where(r => r != null)
                .Select(Id).Distinct().ToList();
            var rooms = result.rooms.Where(r => adjacent.Contains(r.element_id)).ToList();
            if (door.LevelId != level.Id && !rooms.Any()) return;
            string source;
            var data = new DoorData { element_id = Id(door), unique_id = door.UniqueId,
                room_ids = rooms.Select(r => r.element_id).ToList(), clear_width_m = ClearWidth(door, out source), clear_width_source = source };
            XYZ candidate = null;
            try
            {
                candidate = RevitPositions.DoorCandidate(door, level, out var candidateSource);
                data.position_candidate = XY(candidate); data.position_source = candidateSource;
            }
            catch (Exception ex) { Warn(door, "DOOR_NO_POSITION", "無法取得門定位候選：" + ex.Message, "error"); }
            var mapping = settings.doors.SingleOrDefault(d => d.unique_id == door.UniqueId);
            if (mapping?.picked_point != null)
            {
                try
                {
                    RevitPositions.Validate(door, level, mapping.picked_point, false);
                    data.position = mapping.picked_point.point_m.Take(2).ToArray();
                    data.position_source = mapping.picked_point.source; data.position_confirmed = true;
                }
                catch (Exception ex) { Warn(door, "DOOR_POSITION_STALE", ex.Message, "error"); }
            }
            else if (candidate != null && data.position_source == "family_location_point")
            {
                data.position = XY(candidate);
                // 插入點落在主體牆上就是這扇門的門洞；只有對不上時才需要人看
                if (!OnHostWall(door, candidate))
                    Warn(door, "DOOR_INSERTION_POINT", "族群插入點不在主體牆上；請確認門的位置，路線會從這個點進出。");
            }
            // 沒有插入點的門（例如帷幕牆上的門）用外框中心當位置，並提醒是近似值
            else if (candidate != null)
            {
                data.position = XY(candidate);
                Warn(door, "DOOR_POSITION_APPROX", "這扇門沒有插入點（例如帷幕牆上的門），位置用外框中心近似；距離可能有些微誤差。");
            }
            if (data.position == null)
            {
                data.position_confirmed = false;
                result.unresolved_doors.Add(data);
                Warn(door, "DOOR_POSITION_UNCONFIRMED", "門已擷取但讀不到位置，不列入計算；請確認這扇門有幾何。", "error");
                return;
            }
            result.doors.Add(data);
            foreach (var r in rooms) r.door_ids.Add(data.element_id);
            // 開在外牆、一側有房間的門是室外門，本來就只連到一間房
            bool exteriorDoor = rooms.Count == 1 && door.Host is Wall hostWall && hostWall.WallType.Function == WallFunction.Exterior;
            if (rooms.Count < 2 && !exteriorDoor) Warn(door, "DOOR_ROOM_ASSOCIATION_INCOMPLETE", "只找到 " + rooms.Count + " 個本樓層相鄰房間；可能為室外門、未建模通道或房間計算點問題。");
        }
        // 點（只看平面）離主體牆中心線的距離，不超過半個牆厚（再加 1 mm 數值容差）
        static bool OnHostWall(FamilyInstance door, XYZ point)
        {
            if (!(door.Host is Wall wall) || !(wall.Location is LocationCurve location)) return false;
            var curve = location.Curve;
            var hit = curve.Project(new XYZ(point.X, point.Y, curve.GetEndPoint(0).Z));
            return hit != null && hit.Distance <= wall.Width / 2 + UnitUtils.ConvertToInternalUnits(0.001, UnitTypeId.Meters);
        }
        // 非元件式樓梯（舊式草圖樓梯、樓梯族群）讀不到梯段，只能用使用者點的位置或選的防火門（都在「選擇直通梯」設定）；外框用包圍框，只供畫面顯示
        void ReadLegacyStair(Element stair)
        {
            var box = RevitPositions.BoxCorners(stair);
            double x0 = M(box.Min(p => p.X)), x1 = M(box.Max(p => p.X)), y0 = M(box.Min(p => p.Y)), y1 = M(box.Max(p => p.Y));
            var data = new StairData { element_id = Id(stair), unique_id = stair.UniqueId, name = stair.Name,
                footprint = new List<double[]> { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } } };
            data.footprint_loops.Add(data.footprint);
            result.stairs.Add(data);
            var mapping = settings.stairs.SingleOrDefault(x => x.unique_id == stair.UniqueId);
            data.is_direct_stair = mapping?.is_direct_stair;
            if (mapping?.picked_point != null)
            {
                try
                {
                    RevitPositions.Validate(stair, level, mapping.picked_point, true);
                    data.entry_point = mapping.picked_point.point_m.Take(2).ToArray(); data.endpoint_type = mapping.endpoint_type ?? "first_step";
                    data.endpoint_source = mapping.picked_point.source;
                }
                catch (Exception ex) { Warn(stair, "STAIR_ENDPOINT_STALE", ex.Message, "error"); }
            }
            else if (mapping?.endpoint_type == "fire_door" && !string.IsNullOrWhiteSpace(mapping.endpoint_unique_id))
            {
                var door = doc.GetElement(mapping.endpoint_unique_id) as FamilyInstance;
                if (door == null || !MainModel(door) || !Exists(door) || !(door.Location is LocationPoint lp))
                    Warn(stair, "STAIR_ENDPOINT_STALE", "安全梯的防火門已移除或不在本階段；請按「選擇直通梯」重新選。", "error");
                else { data.entry_point = XY(lp.Point); data.endpoint_type = "fire_door"; data.endpoint_source = "user_confirmed:" + door.UniqueId; }
            }
            if (data.entry_point == null && mapping?.is_direct_stair != false)
                Warn(stair, "STAIR_ENDPOINT_UNCONFIRMED", "非元件式樓梯（舊式草圖樓梯或樓梯族群），外掛偵測不到第一階；請按「選擇直通梯」點選這座樓梯，再點第一階。", "error");
            else if (data.entry_point != null && !data.is_direct_stair.HasValue)
                Warn(stair, "STAIR_DIRECT_UNCONFIRMED", "還沒確認是否為直通樓梯；仍列入計算候選，但結果會標需人工確認。請按「選擇直通梯」。");
        }
        void ReadStair(Stairs stair)
        {
            var box = stair.get_BoundingBox(null);
            if (box == null || level.ProjectElevation < stair.BaseElevation - 0.01 || level.ProjectElevation > stair.TopElevation + 0.01) return;
            var data = new StairData { element_id = Id(stair), unique_id = stair.UniqueId, name = stair.Name };
            result.stairs.Add(data);
            foreach (var id in stair.GetStairsRuns())
            {
                var run = (StairsRun)doc.GetElement(id);
                Safe(run, () => {
                    data.footprint_loops.Add(Points(run.GetFootprintBoundary(), true));
                    data.run_paths.Add(Points(run.GetStairsPath(), false));
                });
            }
            foreach (var id in stair.GetStairsLandings())
            {
                var landing = (StairsLanding)doc.GetElement(id);
                Safe(landing, () => data.footprint_loops.Add(Points(landing.GetFootprintBoundary(), true)));
            }
            if (data.footprint_loops.Count == 1) data.footprint = data.footprint_loops[0];
            var mapping = settings.stairs.SingleOrDefault(x => x.unique_id == stair.UniqueId);
            data.is_direct_stair = mapping?.is_direct_stair;
            if (mapping?.picked_point != null)
            {
                try
                {
                    if (mapping.endpoint_type != "first_step" && mapping.endpoint_type != "fire_door")
                        throw new ArgumentException("請選擇第一階或防火門終點類型。");
                    RevitPositions.Validate(stair, level, mapping.picked_point, true);
                    if (mapping.endpoint_type == "fire_door")
                    {
                        var door = string.IsNullOrWhiteSpace(mapping.endpoint_unique_id) ? null : doc.GetElement(mapping.endpoint_unique_id) as FamilyInstance;
                        if (door == null || door.Category?.Id.Value != (long)BuiltInCategory.OST_Doors || !MainModel(door) || !Exists(door))
                            throw new ArgumentException("參考防火門已移除或不在本階段，請重新設定終點。");
                        RevitPositions.Validate(door, level, new PickedPointSetting { point_m = mapping.picked_point.point_m,
                            geometry_fingerprint = mapping.picked_point.reference_geometry_fingerprint }, false);
                    }
                    data.entry_point = mapping.picked_point.point_m.Take(2).ToArray(); data.endpoint_type = mapping.endpoint_type;
                    data.endpoint_source = mapping.picked_point.source;
                }
                catch (Exception ex) { Warn(stair, "STAIR_ENDPOINT_STALE", ex.Message, "error"); }
            }
            else if (mapping != null && !string.IsNullOrWhiteSpace(mapping.endpoint_unique_id))
            {
                var reference = doc.GetElement(mapping.endpoint_unique_id);
                if (reference == null || !MainModel(reference) || !Exists(reference))
                    throw new ArgumentException("樓梯終點參考不存在或不在主模型／所選階段。");
                XYZ point;
                if (mapping.endpoint_type == "fire_door" && reference is FamilyInstance door &&
                    door.Category?.Id.Value == (long)BuiltInCategory.OST_Doors && door.Location is LocationPoint lp)
                    point = lp.Point;
                else if (mapping.endpoint_type == "first_step" && reference is CurveElement curve &&
                    (mapping.curve_end == 0 || mapping.curve_end == 1))
                    point = curve.GeometryCurve.GetEndPoint(mapping.curve_end);
                else throw new ArgumentException("樓梯終點須為 fire_door 門或 first_step 曲線端點（curve_end 0/1）。");
                if (Math.Abs(M(point.Z - level.ProjectElevation)) > 0.5)
                    throw new ArgumentException("樓梯終點不在所選樓層高度附近（0.5 m 檢查容差）。");
                data.entry_point = XY(point); data.endpoint_type = mapping.endpoint_type;
                data.endpoint_source = "user_confirmed:" + reference.UniqueId;
            }
            // An explicit endpoint must be re-confirmed if it fails validation. Automatic
            // detection must not replace it and inherit the old direct-stair qualification.
            bool hasManualEndpoint = mapping?.picked_point != null || !string.IsNullOrWhiteSpace(mapping?.endpoint_unique_id);
            if (data.entry_point == null && !hasManualEndpoint) AutoEndpoint(stair, data);
            if (data.entry_point == null) {
                data.is_direct_stair = data.is_direct_stair == false ? (bool?)false : null;
                Warn(stair, "STAIR_ENDPOINT_UNCONFIRMED", hasManualEndpoint ?
                    "人工樓梯終點無效，請按「選擇直通梯」重新設定；不會改用自動端點並沿用舊確認。" :
                    "偵測不到本層的第一階；請按「選擇直通梯」點選這座樓梯，再點第一階。不會拿樓梯中心點代替。", "error");
            }
            if (!data.is_direct_stair.HasValue) Warn(stair, "STAIR_DIRECT_UNCONFIRMED", "還沒選定是否使用這座樓梯；仍列入計算候選，但結果會標需人工確認。請按「選擇直通梯」。");
        }
        // 沒有手動指定終點時，由外掛偵測本層的第一階。使用者用「選擇直通梯」選過的就直接採用，不再提醒。
        void AutoEndpoint(Stairs stair, StairData data)
        {
            var point = StairEndpoint.Detect(doc, stair, level, out var source, out var diff);
            if (point == null) return;
            data.entry_point = XY(point); data.endpoint_type = "first_step";
            if (data.is_direct_stair == true) { data.endpoint_source = "user_selected_direct:" + source; return; }
            data.endpoint_source = "auto:" + source;
            Warn(stair, "STAIR_ENDPOINT_AUTO", "第一階位置已偵測（" + source + "，和本層高差 "
                + M(diff).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + " m），但還沒選定要用這座；請按「選擇直通梯」。");
        }
        // 使用者選過直通梯後，這一層只算選定的樓梯：取消勾選的和沒選到的都不列入（保留在 unresolved_stairs 供畫面顯示）
        void KeepOnlySelectedStairs()
        {
            bool anySelected = settings.stairs.Any(s => s != null && s.is_direct_stair == true);
            foreach (var stair in result.stairs)
            {
                var mapping = settings.stairs.SingleOrDefault(x => x.unique_id == stair.unique_id);
                if (mapping?.is_direct_stair != false && !(anySelected && mapping == null)) continue;
                stair.entry_point = null; stair.endpoint_type = null; stair.endpoint_source = null; stair.is_direct_stair = false;
                result.warnings.RemoveAll(w => w.element_id == stair.element_id && w.code.StartsWith("STAIR_", StringComparison.Ordinal));
            }
        }
        void ReadCorridor(CorridorSetting setting)
        {
            var room = doc.GetElement(setting.room_unique_id) as Room;
            if (room == null) throw new ArgumentException("走廊 room_unique_id 必須指向房間。");
            Safe(room, () => {
                if (!result.rooms.Any(r => r.unique_id == room.UniqueId)) throw new ArgumentException("走廊房間不在所選樓層／階段。");
                var parts = new List<List<double[]>>();
                foreach (var uid in setting.curve_unique_ids ?? new List<string>())
                {
                    var curve = doc.GetElement(uid) as CurveElement;
                    if (curve == null || !MainModel(curve)) throw new ArgumentException("走廊中心線須指向主模型的 ModelCurve／DetailCurve。");
                    var points = curve.GeometryCurve.Tessellate();
                    if (points.Any(p => Math.Abs(M(p.Z - level.ProjectElevation)) > 0.5))
                        throw new ArgumentException("走廊中心線不在所選樓層高度附近。");
                    parts.Add(points.Select(XY).ToList());
                }
                string source;
                var data = new CorridorData { element_id = Id(room), unique_id = room.UniqueId, name = room.Name,
                    centerline = Geometry.Chain(parts), clear_width_m = ClearWidth(room, out source),
                    clear_width_source = source, rooms_both_sides = setting.rooms_both_sides };
                var roomData = result.rooms.Single(r => r.unique_id == room.UniqueId);
                if (!roomData.boundary_closed || !Geometry.ContainsPath(roomData.boundary, data.centerline))
                    throw new ArgumentException("走廊中心線離開房間輪廓，或房間邊界未能完整表示；未加入路網。");
                result.corridors.Add(data);
                if (!data.clear_width_m.HasValue) Warn(room, "CORRIDOR_NO_CLEAR_WIDTH", "走廊尚未設定有效淨寬。");
                if (!data.rooms_both_sides.HasValue) Warn(room, "CORRIDOR_SIDES_UNCONFIRMED", "走廊兩側是否均有居室尚未確認。");
                Warn(room, "CORRIDOR_PATH_REVIEW", "中心線來自使用者指定曲線，需覆核是否位於可行走區域且未穿牆。");
            });
        }
        void ReadColumns()
        {
            var filter = new ElementMulticategoryFilter(new[] { BuiltInCategory.OST_Columns, BuiltInCategory.OST_StructuralColumns });
            foreach (var column in new FilteredElementCollector(doc).WherePasses(filter).WhereElementIsNotElementType()
                .OrderBy(e => e.Id.Value).Where(e => MainModel(e) && Exists(e)))
                Safe(column, () => {
                    var b = column.get_BoundingBox(null);
                    if (b == null) { Warn(column, "OBSTACLE_NO_GEOMETRY", "柱子無可用包圍框。", "error"); return; }
                    if (b.Max.Z <= level.ProjectElevation || b.Min.Z > level.ProjectElevation + 0.01) return;
                    var corners = new List<XYZ>();
                    foreach (var x in new[] { b.Min.X, b.Max.X }) foreach (var y in new[] { b.Min.Y, b.Max.Y })
                        foreach (var z in new[] { b.Min.Z, b.Max.Z }) corners.Add(b.Transform.OfPoint(new XYZ(x, y, z)));
                    double x0 = M(corners.Min(p => p.X)), x1 = M(corners.Max(p => p.X)), y0 = M(corners.Min(p => p.Y)), y1 = M(corners.Max(p => p.Y));
                    result.obstacles.Add(new ObstacleData { element_id = Id(column), unique_id = column.UniqueId,
                        category = column.Category.Name, geometry_source = "conservative_axis_aligned_bounding_box",
                        footprint = new List<double[]> { new[] { x0, y0 }, new[] { x1, y0 }, new[] { x1, y1 }, new[] { x0, y1 } } });
                    Warn(column, "OBSTACLE_CONSERVATIVE_BOX", "以包圍框保守近似柱子，可能封閉原本可行的通道，需覆核。");
                });
        }
        public ExportSettings Template() => new ExportSettings {
            model_guid = doc.ProjectInformation.UniqueId, level_id = Id(level), phase_id = Id(phase),
            clear_width_parameter = settings.clear_width_parameter,
            rooms = result.rooms.Select(r => new RoomSetting { unique_id = r.unique_id, is_habitable = r.is_habitable, is_circulation = r.is_circulation }).ToList(),
            doors = result.doors.Concat(result.unresolved_doors).Select(d => settings.doors.SingleOrDefault(x => x.unique_id == d.unique_id) ??
                new DoorSetting { unique_id = d.unique_id }).ToList(),
            stairs = result.stairs.Concat(result.unresolved_stairs).Select(s => settings.stairs.SingleOrDefault(x => x.unique_id == s.unique_id) ??
                new StairSetting { unique_id = s.unique_id }).ToList(), corridors = settings.corridors
        };
    }
}
