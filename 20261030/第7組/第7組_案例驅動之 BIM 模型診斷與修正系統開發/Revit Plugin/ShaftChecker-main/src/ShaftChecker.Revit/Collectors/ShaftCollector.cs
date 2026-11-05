using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Recognition;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Collectors
{
    /// <summary>
    /// 從 Revit 模型擷取管道間資料，整理成 Core 的 ShaftInfo。
    /// 哪些空間算管道間，由 ShaftDetector 多訊號評分決定（不只看名稱）；
    /// 主模型與已載入的連結模型都會掃描。所有 Revit API 的讀取都集中在這裡，規則類別不碰 Revit。
    /// </summary>
    public class ShaftCollector
    {
        private readonly Document _doc;
        private readonly RuleConfig _cfg;
        private readonly List<BuiltInCategory> _obstacleCats;
        private List<SourceContext> _sources = new List<SourceContext>();

        /// <summary>最近一次 Collect() 的完整辨識報告（含疑似、未放置房間、孤立開口）。</summary>
        public DetectionReport Report { get; private set; }

        /// <summary>最近一次 Collect() 的機電、結構掃描器（含高層建築物判斷說明）。</summary>
        public ElementScanner Scanner { get; private set; }

        public ShaftCollector(Document doc, RuleConfig cfg)
        {
            _doc = doc;
            _cfg = cfg;
            _obstacleCats = ParseCategories(cfg.ObstacleCategories);
        }

        public List<ShaftInfo> Collect()
        {
            Report = new ShaftDetector(_doc, _cfg).Run();
            _sources = Report.Sources;
            Scanner = new ElementScanner(_doc, _sources, _cfg);

            var options = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };

            var doorsBySource = new Dictionary<SourceContext, List<FamilyInstance>>();
            var result = new List<ShaftInfo>();

            foreach (DetectedSpace ds in Report.Spaces)
            {
                bool include = ds.Detection.Level >= DetectionLevel.Likely ||
                               (_cfg.IncludeCandidateShafts && ds.Detection.Level == DetectionLevel.Candidate);
                if (!include) continue;

                List<FamilyInstance> doors;
                if (!doorsBySource.TryGetValue(ds.Src, out doors))
                {
                    doors = new FilteredElementCollector(ds.Src.Doc)
                        .OfCategory(BuiltInCategory.OST_Doors)
                        .WhereElementIsNotElementType()
                        .OfType<FamilyInstance>()
                        .ToList();
                    doorsBySource[ds.Src] = doors;
                }

                ShaftInfo shaft = Build(ds, doors, options);
                try
                {
                    Scanner.Fill(shaft, ds);
                }
                catch (Exception)
                {
                    // 機電／結構掃描失敗時，仍可執行不需要這些資料的規則
                    shaft.ScanComplete = false;
                }
                result.Add(shaft);
            }

            return result
                .OrderBy(s => s.LevelName)
                .ThenBy(s => s.Number)
                .ToList();
        }

        // ------------------------------------------------------------------ 管道間

        private ShaftInfo Build(DetectedSpace ds, List<FamilyInstance> doors, SpatialElementBoundaryOptions options)
        {
            Room room = ds.Room;
            SourceContext src = ds.Src;

            var shaft = new ShaftInfo
            {
                RoomId = room.Id.GetIdValue(),
                SourceName = src.Name,
                RoomKey = ds.RoomKey,
                Detection = ds.Detection,
                Number = room.Number,
                Name = ShaftDetector.RoomName(room),
                LevelName = room.Level != null ? room.Level.Name : "",
                AreaM2 = UnitConv.ToM2(room.Area),
                BoundaryMm = new List<Point2D>(ds.BoundaryMm)
            };

            IList<IList<BoundarySegment>> loops = null;
            try { loops = room.GetBoundarySegments(options); } catch (Exception) { }
            if (loops != null)
            {
                var wallIds = loops.SelectMany(l => l)
                    .Select(s => s.ElementId)
                    .Where(id => id != null && id != ElementId.InvalidElementId)
                    .Distinct()
                    .ToList();

                foreach (ElementId id in wallIds)
                {
                    var wall = src.Doc.GetElement(id) as Wall;
                    if (wall != null) shaft.Walls.Add(BuildWall(src, wall));
                }
            }

            foreach (FamilyInstance door in doors)
            {
                Room from = src.DoorRoom(door, true);
                Room to = src.DoorRoom(door, false);
                bool fromIsShaft = from != null && from.Id.Equals(room.Id);
                bool toIsShaft = to != null && to.Id.Equals(room.Id);
                if (!fromIsShaft && !toIsShaft) continue;

                Room other = fromIsShaft ? to : from;
                shaft.Doors.Add(BuildDoor(src, door, room, other));
            }

            return shaft;
        }

        // ------------------------------------------------------------------ 牆

        private WallInfo BuildWall(SourceContext src, Wall wall)
        {
            Element type = src.Doc.GetElement(wall.GetTypeId());
            return new WallInfo
            {
                Id = wall.Id.GetIdValue(),
                TypeName = type != null ? type.Name : wall.Name,
                FireRatingRaw = ParamReader.ReadText(wall, type, _cfg.FireRatingParam, BuiltInParameter.FIRE_RATING)
            };
        }

        // ------------------------------------------------------------------ 門

        private DoorInfo BuildDoor(SourceContext src, FamilyInstance door, Room shaftRoom, Room other)
        {
            Element type = door.Symbol;
            Parameter mark = door.get_Parameter(BuiltInParameter.ALL_MODEL_MARK);

            var info = new DoorInfo
            {
                Id = door.Id.GetIdValue(),
                TypeName = type != null ? type.Name : door.Name,
                Mark = mark != null ? mark.AsString() : null,
                FireRatingRaw = ParamReader.ReadText(door, type, _cfg.FireRatingParam, BuiltInParameter.FIRE_RATING),
                SmokeSeal = ParamReader.ReadYesNo(door, type, _cfg.SmokeSealParam),
                WidthMm = ParamReader.ReadLengthMm(door, type, BuiltInParameter.DOOR_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM),
                HeightMm = ParamReader.ReadLengthMm(door, type, BuiltInParameter.DOOR_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM),
                ClearWidthMm = ParamReader.ReadNamedLengthMm(door, type, _cfg.DoorClearWidthParam),
                ClearHeightMm = ParamReader.ReadNamedLengthMm(door, type, _cfg.DoorClearHeightParam),
                FrameWidthMm = ReadFirstLength(door, type, _cfg.DoorFrameWidthParams),
                OtherSideRoomName = ShaftDetector.RoomName(other),
                OtherSideRoomType = ReadRoomType(other),
                OtherSideRoomId = other != null ? (long?)other.Id.GetIdValue() : null
            };

            CheckFrontClearance(src, door, shaftRoom, info);
            return info;
        }

        private static double? ReadFirstLength(Element inst, Element type, List<string> names)
        {
            if (names == null) return null;
            foreach (string n in names)
            {
                double? v = ParamReader.ReadNamedLengthMm(inst, type, n);
                if (v.HasValue && v.Value > 0) return v;
            }
            return null;
        }

        /// <summary>房間的「空間類型」：依設定檔的參數名稱依序讀取（實體、再看類型），都沒有回傳 null。</summary>
        private string ReadRoomType(Room room)
        {
            if (room == null || _cfg.RoomTypeParams == null) return null;
            foreach (string n in _cfg.RoomTypeParams)
            {
                string v = ParamReader.ReadText(room, null, n, null);
                if (!string.IsNullOrEmpty(v)) return v;
            }
            return null;
        }

        /// <summary>
        /// A8：在門的「管道間外側」擠出一個作業區實體盒，查詢與它相交的牆、柱、梁、家具、設備等。
        /// 作業區盒會換算到主模型與每個已載入連結模型的座標中各查一次。
        /// </summary>
        private void CheckFrontClearance(SourceContext src, FamilyInstance door, Room shaftRoom, DoorInfo info)
        {
            var lp = door.Location as LocationPoint;
            if (lp == null) return;

            XYZ p = lp.Point;
            XYZ facing = new XYZ(door.FacingOrientation.X, door.FacingOrientation.Y, 0);
            if (facing.GetLength() < 1e-6) return;
            facing = facing.Normalize();

            var host = door.Host as Wall;
            double halfThick = host != null ? host.Width / 2.0 : UnitConv.FromMm(100);

            // 判斷門的朝向哪一側是管道間：往 facing 方向試一個點，看是否落在管道間 Room 內
            XYZ probe = new XYZ(p.X, p.Y, p.Z + UnitConv.FromMm(1000)) + facing * (halfThick + UnitConv.FromMm(150));
            bool facingIntoShaft = shaftRoom.IsPointInRoom(probe);
            XYZ outDir = facingIntoShaft ? facing.Negate() : facing;

            // 門的平面位置與方向（主模型座標），A9 門頂梁下淨高與 P3 門後方淨空使用
            try
            {
                XYZ hostP = src.ToHost.OfPoint(p);
                XYZ into = src.ToHost.OfVector(outDir.Negate());
                XYZ along = src.ToHost.OfVector(XYZ.BasisZ.CrossProduct(outDir.Negate()));
                // 門檻標高 = 門所在樓層標高 + 窗台（門檻）高度；取不到樓層時用門的位置點
                double sill = ParamReader.ReadLengthMm(door, null, BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM);
                var lvl = src.Doc.GetElement(door.LevelId) as Level;
                double baseFt = lvl != null ? lvl.ProjectElevation + UnitConv.FromMm(sill) : p.Z;
                XYZ hostBase = src.ToHost.OfPoint(new XYZ(p.X, p.Y, baseFt));
                info.CenterX = UnitConv.ToMm(hostP.X);
                info.CenterY = UnitConv.ToMm(hostP.Y);
                info.BaseZ = UnitConv.ToMm(hostBase.Z);
                info.IntoX = into.X; info.IntoY = into.Y;
                info.AlongX = along.X; info.AlongY = along.Y;
                info.WallThicknessMm = UnitConv.ToMm(halfThick * 2);
                info.HasGeometry = true;
            }
            catch (Exception)
            {
                info.HasGeometry = false;
            }

            XYZ side = XYZ.BasisZ.CrossProduct(outDir).Normalize();
            double width = UnitConv.FromMm(info.WidthMm > 0 ? info.WidthMm : 900);
            double depth = UnitConv.FromMm(_cfg.FrontClearanceDepthMm);
            double height = UnitConv.FromMm(Math.Max(_cfg.FrontClearanceHeightMm - 50, 100));

            // 從牆面外 20 mm、樓板上 50 mm 開始，避免碰到門的母牆與樓板
            XYZ basePt = new XYZ(p.X, p.Y, p.Z + UnitConv.FromMm(50)) + outDir * (halfThick + UnitConv.FromMm(20));

            XYZ c0 = basePt + side * (width / 2);
            XYZ c1 = basePt - side * (width / 2);
            XYZ c2 = c1 + outDir * depth;
            XYZ c3 = c0 + outDir * depth;

            Solid box;
            try
            {
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(c0, c1));
                loop.Append(Line.CreateBound(c1, c2));
                loop.Append(Line.CreateBound(c2, c3));
                loop.Append(Line.CreateBound(c3, c0));
                box = GeometryCreationUtilities.CreateExtrusionGeometry(new List<CurveLoop> { loop }, XYZ.BasisZ, height);
            }
            catch (Exception)
            {
                return;
            }

            if (_obstacleCats.Count > 0)
            {
                foreach (SourceContext target in _sources)
                {
                    Solid probeSolid = box;
                    if (!ReferenceEquals(target, src))
                    {
                        try
                        {
                            Transform srcToTarget = target.ToHost.Inverse.Multiply(src.ToHost);
                            probeSolid = SolidUtils.CreateTransformed(box, srcToTarget);
                        }
                        catch (Exception)
                        {
                            continue;
                        }
                    }

                    var exclude = new List<ElementId>();
                    if (ReferenceEquals(target, src))
                    {
                        exclude.Add(door.Id);
                        if (host != null) exclude.Add(host.Id);
                    }

                    IList<Element> hits;
                    try
                    {
                        var collector = new FilteredElementCollector(target.Doc)
                            .WherePasses(new ElementMulticategoryFilter(_obstacleCats))
                            .WhereElementIsNotElementType();
                        if (exclude.Count > 0) collector = collector.Excluding(exclude);
                        hits = collector.WherePasses(new ElementIntersectsSolidFilter(probeSolid)).ToElements();
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    foreach (Element e in hits)
                    {
                        info.FrontObstructions.Add(new Obstruction
                        {
                            Id = e.Id.GetIdValue(),
                            Category = e.Category != null ? e.Category.Name : "",
                            Name = e.Name,
                            Source = target.Name
                        });
                    }
                }
            }

            info.FrontCheckDone = true;
        }

        // ------------------------------------------------------------------ 共用

        private static List<BuiltInCategory> ParseCategories(IEnumerable<string> names)
        {
            var list = new List<BuiltInCategory>();
            if (names == null) return list;
            foreach (string n in names)
            {
                BuiltInCategory bic;
                if (Enum.TryParse(n, out bic)) list.Add(bic);
            }
            return list;
        }
    }
}
