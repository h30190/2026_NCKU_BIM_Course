using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Recognition;
using ShaftChecker.Revit.Services;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Collectors
{
    /// <summary>一個資料來源：主模型，或一個已載入的連結模型。</summary>
    public class SourceContext
    {
        public Document Doc;
        /// <summary>連結模型座標 → 主模型座標；主模型為 Identity。</summary>
        public Transform ToHost;
        /// <summary>連結名稱；主模型為 null。</summary>
        public string Name;
        public Phase DocPhase;

        public bool IsHost { get { return Name == null; } }

        /// <summary>主模型 + 所有已載入的連結模型。</summary>
        public static List<SourceContext> LoadAll(Document host, out int linksTotal)
        {
            var list = new List<SourceContext>
            {
                new SourceContext { Doc = host, ToHost = Transform.Identity, Name = null, DocPhase = FindPhase(host) }
            };

            linksTotal = 0;
            var links = new FilteredElementCollector(host)
                .OfClass(typeof(RevitLinkInstance))
                .Cast<RevitLinkInstance>()
                .ToList();

            foreach (RevitLinkInstance li in links)
            {
                linksTotal++;
                Document ld = li.GetLinkDocument();
                if (ld == null) continue; // 未載入的連結
                list.Add(new SourceContext
                {
                    Doc = ld,
                    ToHost = li.GetTotalTransform(),
                    Name = li.Name,
                    DocPhase = FindPhase(ld)
                });
            }
            return list;
        }

        public static Phase FindPhase(Document doc)
        {
            try
            {
                View view = doc.ActiveView;
                Parameter p = view != null ? view.get_Parameter(BuiltInParameter.VIEW_PHASE) : null;
                if (p != null && p.AsElementId() != ElementId.InvalidElementId)
                {
                    var phase = doc.GetElement(p.AsElementId()) as Phase;
                    if (phase != null) return phase;
                }
                if (doc.Phases.Size > 0) return doc.Phases.get_Item(doc.Phases.Size - 1) as Phase;
            }
            catch (Exception)
            {
                // 取不到階段時改用預設 FromRoom/ToRoom
            }
            return null;
        }

        /// <summary>門的某一側房間（依階段）。</summary>
        public Room DoorRoom(FamilyInstance door, bool from)
        {
            try
            {
                if (DocPhase == null) return from ? door.FromRoom : door.ToRoom;
                return from ? door.get_FromRoom(DocPhase) : door.get_ToRoom(DocPhase);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }

    /// <summary>辨識過程中，一個空間的所有資料。座標一律是主模型座標（mm）。</summary>
    public class DetectedSpace
    {
        public SourceContext Src;
        public Room Room;
        public SpaceFeatures Features = new SpaceFeatures();
        public ShaftDetection Detection;
        public List<Point2D> BoundaryMm = new List<Point2D>();
        public double Z0Mm;
        public double Z1Mm;
        public string RoomKey;
    }

    /// <summary>名稱符合管道間，但沒有放置或沒有封閉（Area = 0）的房間。</summary>
    public class UnplacedRoom
    {
        public long Id;
        public string SourceName;
        public string Number;
        public string Name;
        public string LevelName;
        public string Reason;
    }

    /// <summary>有 Shaft Opening 但任何樓層都沒有房間涵蓋的位置：可能漏放了房間。</summary>
    public class OrphanOpening
    {
        public long Id;
        public string SourceName;
        public double CxMm;
        public double CyMm;
        public double WidthMm;
        public double DepthMm;
        public double Z0Mm;
        public double Z1Mm;
    }

    public class DetectionReport
    {
        public List<SourceContext> Sources = new List<SourceContext>();
        public int LinksTotal;
        public int LinksLoaded;
        public List<DetectedSpace> Spaces = new List<DetectedSpace>();
        public List<UnplacedRoom> Unplaced = new List<UnplacedRoom>();
        public List<OrphanOpening> OrphanOpenings = new List<OrphanOpening>();
        public int MepIndexed;
        public int OpeningsIndexed;
    }

    /// <summary>
    /// 多訊號辨識管道間：把每個 Room 轉成 SpaceFeatures（名稱、參數、尺寸、內含機電元件、
    /// Shaft Opening、上下樓層疊合、門數），交給 Core 的 ShaftClassifier 評分。
    /// 主模型與已載入的連結模型都會掃描。
    /// </summary>
    public class ShaftDetector
    {
        private const double GridCellMm = 2000;

        private static readonly BuiltInCategory[] LinearCats =
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray,
            BuiltInCategory.OST_FlexPipeCurves, BuiltInCategory.OST_FlexDuctCurves
        };

        private static readonly BuiltInCategory[] EquipCats =
        {
            BuiltInCategory.OST_MechanicalEquipment, BuiltInCategory.OST_ElectricalEquipment
        };

        private class MepItem
        {
            public double X, Y, Z0, Z1;
            public bool Vertical;
            public bool Equipment;
        }

        private class OpeningItem
        {
            public long Id;
            public string Source;
            public double Cx, Cy, MinX, MinY, MaxX, MaxY, Z0, Z1;
        }

        private class MepGrid
        {
            private readonly Dictionary<long, List<MepItem>> _cells = new Dictionary<long, List<MepItem>>();

            private static long Key(int ix, int iy)
            {
                return (((long)ix) << 32) ^ (long)(uint)iy;
            }

            public void Add(MepItem item)
            {
                int ix = (int)Math.Floor(item.X / GridCellMm);
                int iy = (int)Math.Floor(item.Y / GridCellMm);
                List<MepItem> list;
                long key = Key(ix, iy);
                if (!_cells.TryGetValue(key, out list))
                {
                    list = new List<MepItem>();
                    _cells[key] = list;
                }
                list.Add(item);
            }

            public IEnumerable<MepItem> Query(double minX, double minY, double maxX, double maxY)
            {
                int x0 = (int)Math.Floor(minX / GridCellMm), x1 = (int)Math.Floor(maxX / GridCellMm);
                int y0 = (int)Math.Floor(minY / GridCellMm), y1 = (int)Math.Floor(maxY / GridCellMm);
                for (int ix = x0; ix <= x1; ix++)
                {
                    for (int iy = y0; iy <= y1; iy++)
                    {
                        List<MepItem> list;
                        if (!_cells.TryGetValue(Key(ix, iy), out list)) continue;
                        foreach (MepItem item in list) yield return item;
                    }
                }
            }
        }

        private readonly Document _doc;
        private readonly RuleConfig _cfg;

        public ShaftDetector(Document doc, RuleConfig cfg)
        {
            _doc = doc;
            _cfg = cfg;
        }

        public DetectionReport Run()
        {
            var report = new DetectionReport();
            int linksTotal;
            report.Sources = SourceContext.LoadAll(_doc, out linksTotal);
            report.LinksTotal = linksTotal;
            report.LinksLoaded = report.Sources.Count - 1;

            Dictionary<string, string> confirms = ConfirmStore.GetAll(ConfirmStore.DocKey(_doc));
            var options = new SpatialElementBoundaryOptions
            {
                SpatialElementBoundaryLocation = SpatialElementBoundaryLocation.Finish
            };

            // 1. 所有空間（主模型 + 連結）
            foreach (SourceContext src in report.Sources)
                CollectRooms(src, options, confirms, report);

            // 2. 機電元件與 Shaft Opening（只有小空間才需要數）
            var small = report.Spaces.Where(s => s.Features.AreaM2 <= _cfg.MaxShaftAreaM2).ToList();
            var grid = new MepGrid();
            var openings = new List<OpeningItem>();
            if (small.Count > 0)
            {
                foreach (SourceContext src in report.Sources)
                    IndexMepAndOpenings(src, grid, openings, report);

                foreach (DetectedSpace s in small)
                {
                    CountMep(s, grid);
                    CountOpenings(s, openings);
                }
            }

            // 3. 有垂直開口、卻沒有任何房間涵蓋
            foreach (OpeningItem o in openings)
            {
                bool covered = report.Spaces.Any(s => PolygonUtils.Contains(s.BoundaryMm, o.Cx, o.Cy));
                if (covered) continue;
                report.OrphanOpenings.Add(new OrphanOpening
                {
                    Id = o.Id,
                    SourceName = o.Source,
                    CxMm = o.Cx,
                    CyMm = o.Cy,
                    WidthMm = o.MaxX - o.MinX,
                    DepthMm = o.MaxY - o.MinY,
                    Z0Mm = o.Z0,
                    Z1Mm = o.Z1
                });
            }

            // 4. 上下樓層疊合
            ShaftClassifier.AssignStacks(report.Spaces.Select(s => s.Features).ToList(), _cfg.MaxShaftAreaM2);

            // 5. 評分
            foreach (DetectedSpace s in report.Spaces)
                s.Detection = ShaftClassifier.Classify(s.Features, _cfg);

            return report;
        }

        // ------------------------------------------------------------------ 空間

        private void CollectRooms(SourceContext src, SpatialElementBoundaryOptions options,
            Dictionary<string, string> confirms, DetectionReport report)
        {
            var doorCount = CountDoors(src);

            var rooms = new FilteredElementCollector(src.Doc)
                .OfCategory(BuiltInCategory.OST_Rooms)
                .WhereElementIsNotElementType()
                .OfType<Room>()
                .ToList();

            foreach (Room r in rooms)
            {
                string name = RoomName(r);
                string levelName = r.Level != null ? r.Level.Name : "";

                if (r.Area <= 0 || r.Location == null)
                {
                    if (RuleConfig.MatchesAny(name, _cfg.ShaftNamePatterns) ||
                        RuleConfig.MatchesAny(r.Number, _cfg.ShaftNamePatterns))
                    {
                        report.Unplaced.Add(new UnplacedRoom
                        {
                            Id = r.Id.GetIdValue(),
                            SourceName = src.Name,
                            Number = r.Number,
                            Name = name,
                            LevelName = levelName,
                            Reason = r.Location == null
                                ? "房間尚未放置（Not Placed）"
                                : "房間未封閉（Not Enclosed），面積為 0，請檢查周圍牆是否接合"
                        });
                    }
                    continue;
                }

                IList<IList<BoundarySegment>> loops = null;
                try { loops = r.GetBoundarySegments(options); } catch (Exception) { }
                if (loops == null || loops.Count == 0) continue;

                var ds = new DetectedSpace { Src = src, Room = r };
                foreach (BoundarySegment seg in loops[0])
                {
                    IList<XYZ> pts = seg.GetCurve().Tessellate();
                    for (int i = 0; i < pts.Count - 1; i++)
                    {
                        XYZ p = src.ToHost.OfPoint(pts[i]);
                        ds.BoundaryMm.Add(new Point2D(UnitConv.ToMm(p.X), UnitConv.ToMm(p.Y)));
                    }
                }
                if (ds.BoundaryMm.Count < 3) continue;

                SetZRange(ds, src, r);

                RectResult rect = PolygonUtils.MinAreaRectangle(ds.BoundaryMm);
                Point2D c = PolygonUtils.Centroid(ds.BoundaryMm);
                ds.RoomKey = ConfirmStore.RoomKey(src.Name, r.UniqueId);

                string confirm;
                confirms.TryGetValue(ds.RoomKey, out confirm);

                int doors;
                doorCount.TryGetValue(r.Id.GetIdValue(), out doors);

                ds.Features = new SpaceFeatures
                {
                    RoomId = r.Id.GetIdValue(),
                    SourceName = src.Name,
                    Number = r.Number,
                    Name = name,
                    LevelName = levelName,
                    AreaM2 = UnitConv.ToM2(r.Area),
                    ShortSideMm = rect.ShortSide,
                    LongSideMm = rect.LongSide,
                    CentroidXMm = c.X,
                    CentroidYMm = c.Y,
                    ParameterHit = ScanParameters(r),
                    UserConfirm = confirm,
                    DoorCount = doors
                };

                report.Spaces.Add(ds);
            }
        }

        private static void SetZRange(DetectedSpace ds, SourceContext src, Room r)
        {
            BoundingBoxXYZ bb = null;
            try { bb = r.get_BoundingBox(null); } catch (Exception) { }

            if (bb != null)
            {
                double a = src.ToHost.OfPoint(bb.Min).Z;
                double b = src.ToHost.OfPoint(bb.Max).Z;
                ds.Z0Mm = UnitConv.ToMm(Math.Min(a, b));
                ds.Z1Mm = UnitConv.ToMm(Math.Max(a, b));
            }
            else
            {
                double el = r.Level != null ? r.Level.Elevation : 0;
                double z = src.ToHost.OfPoint(new XYZ(0, 0, el)).Z;
                ds.Z0Mm = UnitConv.ToMm(z);
                ds.Z1Mm = ds.Z0Mm + 3000;
            }
        }

        private static Dictionary<long, int> CountDoors(SourceContext src)
        {
            var counts = new Dictionary<long, int>();
            var doors = new FilteredElementCollector(src.Doc)
                .OfCategory(BuiltInCategory.OST_Doors)
                .WhereElementIsNotElementType()
                .OfType<FamilyInstance>();

            foreach (FamilyInstance door in doors)
            {
                Room a = src.DoorRoom(door, true);
                Room b = src.DoorRoom(door, false);
                if (a != null) Inc(counts, a.Id.GetIdValue());
                if (b != null && (a == null || !a.Id.Equals(b.Id))) Inc(counts, b.Id.GetIdValue());
            }
            return counts;
        }

        private static void Inc(Dictionary<long, int> d, long key)
        {
            int n;
            d.TryGetValue(key, out n);
            d[key] = n + 1;
        }

        internal static string RoomName(Room r)
        {
            if (r == null) return null;
            Parameter p = r.get_Parameter(BuiltInParameter.ROOM_NAME);
            return p != null ? p.AsString() : r.Name;
        }

        /// <summary>房間的文字參數（備註、部門、用途…）中，有沒有提到管道間。回傳「參數名＝內容」。</summary>
        private string ScanParameters(Room r)
        {
            foreach (Parameter p in r.Parameters)
            {
                try
                {
                    if (p.StorageType != StorageType.String) continue;
                    Definition def = p.Definition;
                    if (def == null) continue;

                    var idef = def as InternalDefinition;
                    if (idef != null &&
                        (idef.BuiltInParameter == BuiltInParameter.ROOM_NAME ||
                         idef.BuiltInParameter == BuiltInParameter.ROOM_NUMBER))
                        continue;

                    if (def.Name.StartsWith("PS_", StringComparison.Ordinal)) continue; // 本外掛寫回的欄位

                    string v = p.AsString();
                    if (string.IsNullOrWhiteSpace(v)) continue;
                    if (RuleConfig.MatchesAny(v, _cfg.ShaftKeywordPatterns))
                        return def.Name + "＝" + (v.Length > 30 ? v.Substring(0, 30) + "…" : v);
                }
                catch (Exception)
                {
                    // 個別參數讀取失敗時略過
                }
            }
            return null;
        }

        // ------------------------------------------------------------------ 機電元件、Shaft Opening

        private void IndexMepAndOpenings(SourceContext src, MepGrid grid, List<OpeningItem> openings, DetectionReport report)
        {
            var equipIds = new HashSet<long>(EquipCats.Select(c => (long)(int)c));
            var cats = LinearCats.Concat(EquipCats).ToList();

            var mep = new FilteredElementCollector(src.Doc)
                .WherePasses(new ElementMulticategoryFilter(cats))
                .WhereElementIsNotElementType();

            foreach (Element e in mep)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch (Exception) { }
                if (bb == null) continue;

                XYZ a = src.ToHost.OfPoint(bb.Min);
                XYZ b = src.ToHost.OfPoint(bb.Max);

                double dz = UnitConv.ToMm(bb.Max.Z - bb.Min.Z);
                double dx = UnitConv.ToMm(bb.Max.X - bb.Min.X);
                double dy = UnitConv.ToMm(bb.Max.Y - bb.Min.Y);

                bool equipment = e.Category != null && equipIds.Contains(e.Category.Id.GetIdValue());

                grid.Add(new MepItem
                {
                    X = UnitConv.ToMm((a.X + b.X) / 2),
                    Y = UnitConv.ToMm((a.Y + b.Y) / 2),
                    Z0 = UnitConv.ToMm(Math.Min(a.Z, b.Z)),
                    Z1 = UnitConv.ToMm(Math.Max(a.Z, b.Z)),
                    Equipment = equipment,
                    Vertical = !equipment && dz >= 1500 && dz >= 2 * Math.Max(dx, dy)
                });
                report.MepIndexed++;
            }

            var ops = new FilteredElementCollector(src.Doc)
                .OfCategory(BuiltInCategory.OST_ShaftOpening)
                .WhereElementIsNotElementType();

            foreach (Element e in ops)
            {
                BoundingBoxXYZ bb = null;
                try { bb = e.get_BoundingBox(null); } catch (Exception) { }
                if (bb == null) continue;

                XYZ a = src.ToHost.OfPoint(bb.Min);
                XYZ b = src.ToHost.OfPoint(bb.Max);
                double minX = UnitConv.ToMm(Math.Min(a.X, b.X)), maxX = UnitConv.ToMm(Math.Max(a.X, b.X));
                double minY = UnitConv.ToMm(Math.Min(a.Y, b.Y)), maxY = UnitConv.ToMm(Math.Max(a.Y, b.Y));

                openings.Add(new OpeningItem
                {
                    Id = e.Id.GetIdValue(),
                    Source = src.Name,
                    Cx = (minX + maxX) / 2,
                    Cy = (minY + maxY) / 2,
                    MinX = minX, MaxX = maxX, MinY = minY, MaxY = maxY,
                    Z0 = UnitConv.ToMm(Math.Min(a.Z, b.Z)),
                    Z1 = UnitConv.ToMm(Math.Max(a.Z, b.Z))
                });
                report.OpeningsIndexed++;
            }
        }

        private static void CountMep(DetectedSpace s, MepGrid grid)
        {
            double minX = s.BoundaryMm.Min(p => p.X), maxX = s.BoundaryMm.Max(p => p.X);
            double minY = s.BoundaryMm.Min(p => p.Y), maxY = s.BoundaryMm.Max(p => p.Y);

            int linear = 0, vertical = 0, equip = 0;
            foreach (MepItem item in grid.Query(minX, minY, maxX, maxY))
            {
                if (item.Z1 < s.Z0Mm || item.Z0 > s.Z1Mm) continue;
                if (!PolygonUtils.Contains(s.BoundaryMm, item.X, item.Y)) continue;

                if (item.Equipment) equip++;
                else
                {
                    linear++;
                    if (item.Vertical) vertical++;
                }
            }

            s.Features.MepLinearCount = linear;
            s.Features.MepVerticalCount = vertical;
            s.Features.MepEquipmentCount = equip;
        }

        private static void CountOpenings(DetectedSpace s, List<OpeningItem> openings)
        {
            int count = 0;
            foreach (OpeningItem o in openings)
            {
                if (o.Z1 < s.Z0Mm || o.Z0 > s.Z1Mm) continue;

                bool roomHasOpeningCenter = PolygonUtils.Contains(s.BoundaryMm, o.Cx, o.Cy);
                bool openingHasRoomCentroid =
                    s.Features.CentroidXMm >= o.MinX && s.Features.CentroidXMm <= o.MaxX &&
                    s.Features.CentroidYMm >= o.MinY && s.Features.CentroidYMm <= o.MaxY;

                if (roomHasOpeningCenter || openingHasRoomCentroid) count++;
            }
            s.Features.ShaftOpeningCount = count;
        }
    }
}
