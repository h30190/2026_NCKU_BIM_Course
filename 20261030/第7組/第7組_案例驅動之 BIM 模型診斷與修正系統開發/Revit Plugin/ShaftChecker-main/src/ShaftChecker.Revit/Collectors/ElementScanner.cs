using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Architecture;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;
using ShaftChecker.Revit.Utils;

namespace ShaftChecker.Revit.Collectors
{
    /// <summary>
    /// 掃描管道間內與附近的機電管線、梁、柱、隔牆、風管附件，以及樓層資訊（本層／上一層樓板、地下層、高層建築物），
    /// 填進 ShaftInfo。主模型與已載入的連結模型都會掃描，座標一律換算成主模型 mm。
    /// </summary>
    public class ElementScanner
    {
        private static readonly BuiltInCategory[] MepCats =
        {
            BuiltInCategory.OST_PipeCurves, BuiltInCategory.OST_DuctCurves,
            BuiltInCategory.OST_Conduit, BuiltInCategory.OST_CableTray
        };

        private static readonly BuiltInCategory[] ColumnCats =
        {
            BuiltInCategory.OST_StructuralColumns, BuiltInCategory.OST_Columns
        };

        private static readonly BuiltInCategory[] AccessoryCats =
        {
            BuiltInCategory.OST_DuctAccessory, BuiltInCategory.OST_GenericModel
        };

        private readonly List<SourceContext> _sources;
        private readonly RuleConfig _cfg;
        private readonly List<double> _hostLevelZ = new List<double>();
        private readonly Dictionary<string, BeamInfo> _beamCache = new Dictionary<string, BeamInfo>();
        private readonly Dictionary<string, ColumnInfo> _columnCache = new Dictionary<string, ColumnInfo>();
        private readonly Dictionary<string, MepInfo> _mepCache = new Dictionary<string, MepInfo>();

        public bool StructureFound { get; private set; }
        public bool MepFound { get; private set; }
        public bool IsHighRise { get; private set; }
        /// <summary>高層建築物判斷的說明，例如「自動判斷：地上 18 層、高 62 m → 高層建築物」。</summary>
        public string HighRiseNote { get; private set; }

        public ElementScanner(Document host, List<SourceContext> sources, RuleConfig cfg)
        {
            _sources = sources;
            _cfg = cfg;

            StructureFound = sources.Any(s => HasAny(s.Doc, BuiltInCategory.OST_StructuralFraming) ||
                                              HasAny(s.Doc, BuiltInCategory.OST_StructuralColumns));
            MepFound = sources.Any(s => MepCats.Any(c => HasAny(s.Doc, c)));

            var levels = new FilteredElementCollector(host).OfClass(typeof(Level)).Cast<Level>().ToList();
            foreach (Level l in levels) _hostLevelZ.Add(UnitConv.ToMm(l.ProjectElevation));
            _hostLevelZ.Sort();
            DecideHighRise(levels);
        }

        private static bool HasAny(Document doc, BuiltInCategory cat)
        {
            try
            {
                return new FilteredElementCollector(doc).OfCategory(cat).WhereElementIsNotElementType().FirstElementId()
                       != ElementId.InvalidElementId;
            }
            catch (Exception)
            {
                return false;
            }
        }

        private void DecideHighRise(List<Level> levels)
        {
            string mode = (_cfg.HighRiseMode ?? "auto").Trim().ToLowerInvariant();
            if (mode == "yes" || mode == "true") { IsHighRise = true; HighRiseNote = "設定檔指定為高層建築物"; return; }
            if (mode == "no" || mode == "false") { IsHighRise = false; HighRiseNote = "設定檔指定為非高層建築物"; return; }

            var elevs = levels.Select(l => UnitConv.ToMm(l.Elevation)).OrderBy(z => z).ToList();
            int above = elevs.Count(z => z >= -100);
            int floors = Math.Max(0, above - 1); // 最上面的樓層線通常是屋頂
            double height = elevs.Count > 0 ? elevs.Max() - Math.Max(0, elevs.Where(z => z >= -100).DefaultIfEmpty(0).Min()) : 0;
            IsHighRise = floors >= _cfg.HighRiseFloors || height >= _cfg.HighRiseHeightMm;
            HighRiseNote = "自動判斷：地上約 " + floors + " 層、高約 " + (height / 1000).ToString("F0") + " m → " +
                           (IsHighRise ? "高層建築物" : "非高層建築物") + "（16 層或 50 m 以上為高層，可在設定檔 highRiseMode 指定）";
        }

        // ------------------------------------------------------------------ 主流程

        public void Fill(ShaftInfo s, DetectedSpace ds)
        {
            s.StructureModelFound = StructureFound;
            s.MepModelFound = MepFound;
            s.IsHighRise = IsHighRise;

            s.FloorZ = ds.Z0Mm;
            double next = _hostLevelZ.Where(z => z > ds.Z0Mm + 500).DefaultIfEmpty(double.NaN).Min();
            s.CeilingZ = double.IsNaN(next) ? Math.Max(ds.Z1Mm, ds.Z0Mm + 2500) : next;
            s.IsUnderground = IsUnderground(ds.Room);

            if (s.BoundaryMm.Count < 3) return;

            double search = Math.Max(300, _cfg.StructSearchMm);
            double minX = s.BoundaryMm.Min(p => p.X) - search, maxX = s.BoundaryMm.Max(p => p.X) + search;
            double minY = s.BoundaryMm.Min(p => p.Y) - search, maxY = s.BoundaryMm.Max(p => p.Y) + search;
            double minZ = s.FloorZ - 300, maxZ = s.CeilingZ + 300;

            foreach (SourceContext src in _sources)
            {
                BoundingBoxIntersectsFilter box = BoxFilter(src, minX, minY, minZ, maxX, maxY, maxZ);
                if (box == null) continue;

                CollectMep(src, box, s);
                if (StructureFound)
                {
                    CollectBeams(src, box, s);
                    CollectColumns(src, box, s);
                }
                CollectWalls(src, box, s);
                CollectAccessories(src, box, s);
            }
        }

        private bool IsUnderground(Room room)
        {
            Level lv = room != null ? room.Level : null;
            if (lv == null) return false;
            if (RuleConfig.MatchesAny(lv.Name, _cfg.BasementLevelPatterns)) return true;
            try { return UnitConv.ToMm(lv.Elevation) < _cfg.BasementElevationMm; }
            catch (Exception) { return false; }
        }

        /// <summary>主模型 mm 的搜尋範圍 → 該來源文件座標的 Outline。</summary>
        private static BoundingBoxIntersectsFilter BoxFilter(SourceContext src, double x0, double y0, double z0,
            double x1, double y1, double z1)
        {
            try
            {
                Transform inv = src.ToHost.Inverse;
                double fx0 = UnitConv.FromMm(x0), fy0 = UnitConv.FromMm(y0), fz0 = UnitConv.FromMm(z0);
                double fx1 = UnitConv.FromMm(x1), fy1 = UnitConv.FromMm(y1), fz1 = UnitConv.FromMm(z1);
                double ax = double.MaxValue, ay = double.MaxValue, az = double.MaxValue;
                double bx = double.MinValue, by = double.MinValue, bz = double.MinValue;
                foreach (double x in new[] { fx0, fx1 })
                    foreach (double y in new[] { fy0, fy1 })
                        foreach (double z in new[] { fz0, fz1 })
                        {
                            XYZ p = inv.OfPoint(new XYZ(x, y, z));
                            ax = Math.Min(ax, p.X); ay = Math.Min(ay, p.Y); az = Math.Min(az, p.Z);
                            bx = Math.Max(bx, p.X); by = Math.Max(by, p.Y); bz = Math.Max(bz, p.Z);
                        }
                return new BoundingBoxIntersectsFilter(new Outline(new XYZ(ax, ay, az), new XYZ(bx, by, bz)));
            }
            catch (Exception)
            {
                return null;
            }
        }

        private static string Key(SourceContext src, Element e)
        {
            return (src.Name ?? "") + "|" + e.Id.GetIdValue();
        }

        private static bool IsCat(Element e, BuiltInCategory bic)
        {
            return e.Category != null && e.Category.Id.GetIdValue() == (long)(int)bic;
        }

        // ------------------------------------------------------------------ 機電管線

        private void CollectMep(SourceContext src, BoundingBoxIntersectsFilter box, ShaftInfo s)
        {
            IList<Element> list;
            try
            {
                list = new FilteredElementCollector(src.Doc)
                    .WherePasses(new ElementMulticategoryFilter(MepCats))
                    .WhereElementIsNotElementType()
                    .WherePasses(box)
                    .ToElements();
            }
            catch (Exception) { return; }

            foreach (Element e in list)
            {
                string key = Key(src, e);
                MepInfo info;
                if (!_mepCache.TryGetValue(key, out info))
                {
                    info = ReadMep(src, e);
                    _mepCache[key] = info;
                }
                if (info != null) s.Mep.Add(info);
            }
        }

        private MepInfo ReadMep(SourceContext src, Element e)
        {
            var mc = e as MEPCurve;
            if (mc == null) return null;
            var lc = mc.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return null;

            XYZ a = src.ToHost.OfPoint(lc.Curve.GetEndPoint(0));
            XYZ b = src.ToHost.OfPoint(lc.Curve.GetEndPoint(1));

            MepKind kind;
            if (IsCat(e, BuiltInCategory.OST_PipeCurves)) kind = MepKind.Pipe;
            else if (IsCat(e, BuiltInCategory.OST_DuctCurves)) kind = MepKind.Duct;
            else if (IsCat(e, BuiltInCategory.OST_Conduit)) kind = MepKind.Conduit;
            else kind = MepKind.CableTray;

            var info = new MepInfo
            {
                Id = e.Id.GetIdValue(),
                Source = src.Name,
                Kind = kind,
                X1 = UnitConv.ToMm(a.X), Y1 = UnitConv.ToMm(a.Y), Z1 = UnitConv.ToMm(a.Z),
                X2 = UnitConv.ToMm(b.X), Y2 = UnitConv.ToMm(b.Y), Z2 = UnitConv.ToMm(b.Z)
            };

            switch (kind)
            {
                case MepKind.Pipe:
                    info.OuterDiameterMm = ParamReader.ReadLengthMm(e, null,
                        BuiltInParameter.RBS_PIPE_OUTER_DIAMETER, BuiltInParameter.RBS_PIPE_DIAMETER_PARAM);
                    break;
                case MepKind.Conduit:
                    info.OuterDiameterMm = ParamReader.ReadLengthMm(e, null,
                        BuiltInParameter.RBS_CONDUIT_OUTER_DIAM_PARAM, BuiltInParameter.RBS_CONDUIT_DIAMETER_PARAM);
                    break;
                case MepKind.Duct:
                    info.OuterDiameterMm = ParamReader.ReadLengthMm(e, null, BuiltInParameter.RBS_CURVE_DIAMETER_PARAM);
                    if (info.OuterDiameterMm <= 0)
                    {
                        info.WidthMm = ParamReader.ReadLengthMm(e, null, BuiltInParameter.RBS_CURVE_WIDTH_PARAM);
                        info.HeightMm = ParamReader.ReadLengthMm(e, null, BuiltInParameter.RBS_CURVE_HEIGHT_PARAM);
                    }
                    break;
                default:
                    info.WidthMm = ParamReader.ReadLengthMm(e, null, BuiltInParameter.RBS_CABLETRAY_WIDTH_PARAM);
                    info.HeightMm = ParamReader.ReadLengthMm(e, null, BuiltInParameter.RBS_CABLETRAY_HEIGHT_PARAM);
                    break;
            }
            if (info.OuterDiameterMm <= 0 && info.WidthMm <= 0 && info.HeightMm <= 0) info.OuterDiameterMm = 50;

            info.InsulationMm = InsulationOf(src.Doc, e);

            string sysText = SystemText(src.Doc, e);
            info.Group = MepClassifier.Classify(kind, sysText, _cfg);
            info.SystemName = sysText.Length > 60 ? sysText.Substring(0, 60) : sysText;

            // 垂直的矩形元件（風管、電纜架）以外框當平面外形
            if (info.IsVertical && !info.IsRound) info.FootprintMm = BoxFootprint(src, e);
            return info;
        }

        private static double InsulationOf(Document doc, Element e)
        {
            double t = 0;
            try
            {
                foreach (ElementId id in InsulationLiningBase.GetInsulationIds(doc, e.Id))
                {
                    var ins = doc.GetElement(id) as InsulationLiningBase;
                    if (ins != null) t = Math.Max(t, UnitConv.ToMm(ins.Thickness));
                }
            }
            catch (Exception)
            {
                // 沒有保溫或 API 不支援
            }
            return t;
        }

        private static string SystemText(Document doc, Element e)
        {
            var parts = new List<string>();
            AddParam(parts, e.get_Parameter(BuiltInParameter.RBS_SYSTEM_CLASSIFICATION_PARAM));
            AddParam(parts, e.get_Parameter(BuiltInParameter.RBS_SYSTEM_NAME_PARAM));
            foreach (BuiltInParameter bip in new[] { BuiltInParameter.RBS_PIPING_SYSTEM_TYPE_PARAM, BuiltInParameter.RBS_DUCT_SYSTEM_TYPE_PARAM })
            {
                try
                {
                    Parameter p = e.get_Parameter(bip);
                    if (p == null || !p.HasValue || p.StorageType != StorageType.ElementId) continue;
                    Element t = doc.GetElement(p.AsElementId());
                    if (t != null && !string.IsNullOrEmpty(t.Name)) parts.Add(t.Name);
                }
                catch (Exception) { }
            }
            Element type = doc.GetElement(e.GetTypeId());
            if (type != null && !string.IsNullOrEmpty(type.Name)) parts.Add(type.Name);
            return string.Join(" ", parts.Distinct());
        }

        private static void AddParam(List<string> parts, Parameter p)
        {
            if (p == null || !p.HasValue) return;
            string v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            if (!string.IsNullOrWhiteSpace(v)) parts.Add(v.Trim());
        }

        private static List<Point2D> BoxFootprint(SourceContext src, Element e)
        {
            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(null); } catch (Exception) { }
            if (bb == null) return null;
            double ax = double.MaxValue, ay = double.MaxValue, bx = double.MinValue, by = double.MinValue;
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                {
                    XYZ p = src.ToHost.OfPoint(new XYZ(x, y, bb.Min.Z));
                    ax = Math.Min(ax, p.X); ay = Math.Min(ay, p.Y); bx = Math.Max(bx, p.X); by = Math.Max(by, p.Y);
                }
            double x0 = UnitConv.ToMm(ax), y0 = UnitConv.ToMm(ay), x1 = UnitConv.ToMm(bx), y1 = UnitConv.ToMm(by);
            return new List<Point2D> { new Point2D(x0, y0), new Point2D(x1, y0), new Point2D(x1, y1), new Point2D(x0, y1) };
        }

        // ------------------------------------------------------------------ 梁、柱

        private void CollectBeams(SourceContext src, BoundingBoxIntersectsFilter box, ShaftInfo s)
        {
            IList<Element> list;
            try
            {
                list = new FilteredElementCollector(src.Doc)
                    .OfCategory(BuiltInCategory.OST_StructuralFraming)
                    .WhereElementIsNotElementType()
                    .WherePasses(box)
                    .ToElements();
            }
            catch (Exception) { return; }

            foreach (Element e in list)
            {
                string key = Key(src, e);
                BeamInfo b;
                if (!_beamCache.TryGetValue(key, out b))
                {
                    b = ReadBeam(src, e);
                    _beamCache[key] = b;
                }
                if (b != null) s.Beams.Add(b);
            }
        }

        private static BeamInfo ReadBeam(SourceContext src, Element e)
        {
            var lc = e.Location as LocationCurve;
            if (lc == null || lc.Curve == null) return null;
            XYZ a = src.ToHost.OfPoint(lc.Curve.GetEndPoint(0));
            XYZ b = src.ToHost.OfPoint(lc.Curve.GetEndPoint(1));
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6) return null; // 垂直構件不當梁處理
            double ux = dx / len, uy = dy / len, vx = -uy, vy = ux;

            var pts = new List<XYZ>();
            CollectVertices(e, src.ToHost, pts);
            if (pts.Count == 0) BoxCorners(src, e, pts);
            if (pts.Count == 0) return null;

            double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (XYZ p in pts)
            {
                double pu = (p.X - a.X) * ux + (p.Y - a.Y) * uy;
                double pv = (p.X - a.X) * vx + (p.Y - a.Y) * vy;
                minU = Math.Min(minU, pu); maxU = Math.Max(maxU, pu);
                minV = Math.Min(minV, pv); maxV = Math.Max(maxV, pv);
                minZ = Math.Min(minZ, p.Z); maxZ = Math.Max(maxZ, p.Z);
            }
            double midV = (minV + maxV) / 2;
            XYZ p1 = new XYZ(a.X + ux * minU + vx * midV, a.Y + uy * minU + vy * midV, 0);
            XYZ p2 = new XYZ(a.X + ux * maxU + vx * midV, a.Y + uy * maxU + vy * midV, 0);

            Element type = e.Document.GetElement(e.GetTypeId());
            return new BeamInfo
            {
                Id = e.Id.GetIdValue(),
                Source = src.Name,
                Name = type != null ? type.Name : e.Name,
                X1 = UnitConv.ToMm(p1.X), Y1 = UnitConv.ToMm(p1.Y),
                X2 = UnitConv.ToMm(p2.X), Y2 = UnitConv.ToMm(p2.Y),
                WidthMm = UnitConv.ToMm(maxV - minV),
                BottomZ = UnitConv.ToMm(minZ),
                TopZ = UnitConv.ToMm(maxZ)
            };
        }

        private void CollectColumns(SourceContext src, BoundingBoxIntersectsFilter box, ShaftInfo s)
        {
            IList<Element> list;
            try
            {
                list = new FilteredElementCollector(src.Doc)
                    .WherePasses(new ElementMulticategoryFilter(ColumnCats))
                    .WhereElementIsNotElementType()
                    .WherePasses(box)
                    .ToElements();
            }
            catch (Exception) { return; }

            foreach (Element e in list)
            {
                string key = Key(src, e);
                ColumnInfo c;
                if (!_columnCache.TryGetValue(key, out c))
                {
                    c = ReadColumn(src, e);
                    _columnCache[key] = c;
                }
                if (c != null) s.Columns.Add(c);
            }
        }

        private static ColumnInfo ReadColumn(SourceContext src, Element e)
        {
            var pts = new List<XYZ>();
            CollectVertices(e, src.ToHost, pts);
            if (pts.Count == 0) BoxCorners(src, e, pts);
            if (pts.Count < 3) return null;

            var hull = PolygonUtils.ConvexHull(pts.Select(p => new Point2D(UnitConv.ToMm(p.X), UnitConv.ToMm(p.Y))).ToList());
            if (hull.Count < 3) return null;
            Element type = e.Document.GetElement(e.GetTypeId());
            return new ColumnInfo
            {
                Id = e.Id.GetIdValue(),
                Source = src.Name,
                Name = type != null ? type.Name : e.Name,
                FootprintMm = hull,
                BottomZ = UnitConv.ToMm(pts.Min(p => p.Z)),
                TopZ = UnitConv.ToMm(pts.Max(p => p.Z))
            };
        }

        /// <summary>收集元件實體的所有頂點（邊的細分點），換算到主模型座標。</summary>
        private static void CollectVertices(Element e, Transform toHost, List<XYZ> output)
        {
            GeometryElement ge = null;
            try
            {
                var opt = new Options { DetailLevel = ViewDetailLevel.Fine, ComputeReferences = false, IncludeNonVisibleObjects = false };
                ge = e.get_Geometry(opt);
            }
            catch (Exception) { }
            if (ge != null) Walk(ge, toHost, output, 0);
        }

        private static void Walk(GeometryElement ge, Transform toHost, List<XYZ> output, int depth)
        {
            if (depth > 4) return;
            foreach (GeometryObject go in ge)
            {
                var solid = go as Solid;
                if (solid != null)
                {
                    if (solid.Volume <= 1e-9) continue;
                    foreach (Edge edge in solid.Edges)
                    {
                        foreach (XYZ p in edge.Tessellate()) output.Add(toHost.OfPoint(p));
                    }
                    continue;
                }
                var gi = go as GeometryInstance;
                if (gi != null)
                {
                    GeometryElement inst = null;
                    try { inst = gi.GetInstanceGeometry(); } catch (Exception) { }
                    if (inst != null) Walk(inst, toHost, output, depth + 1);
                }
            }
        }

        private static void BoxCorners(SourceContext src, Element e, List<XYZ> output)
        {
            BoundingBoxXYZ bb = null;
            try { bb = e.get_BoundingBox(null); } catch (Exception) { }
            if (bb == null) return;
            foreach (double x in new[] { bb.Min.X, bb.Max.X })
                foreach (double y in new[] { bb.Min.Y, bb.Max.Y })
                    foreach (double z in new[] { bb.Min.Z, bb.Max.Z })
                        output.Add(src.ToHost.OfPoint(new XYZ(x, y, z)));
        }

        // ------------------------------------------------------------------ 牆、風管附件

        private static void CollectWalls(SourceContext src, BoundingBoxIntersectsFilter box, ShaftInfo s)
        {
            IList<Element> list;
            try
            {
                list = new FilteredElementCollector(src.Doc)
                    .OfCategory(BuiltInCategory.OST_Walls)
                    .WhereElementIsNotElementType()
                    .WherePasses(box)
                    .ToElements();
            }
            catch (Exception) { return; }

            foreach (Element e in list)
            {
                var w = e as Wall;
                if (w == null) continue;
                var lc = w.Location as LocationCurve;
                var line = lc != null ? lc.Curve as Line : null;
                if (line == null) continue;

                XYZ a = src.ToHost.OfPoint(line.GetEndPoint(0));
                XYZ b = src.ToHost.OfPoint(line.GetEndPoint(1));
                double z0 = UnitConv.ToMm(a.Z), z1 = z0 + 3000;
                BoundingBoxXYZ bb = null;
                try { bb = w.get_BoundingBox(null); } catch (Exception) { }
                if (bb != null)
                {
                    double za = src.ToHost.OfPoint(bb.Min).Z, zb = src.ToHost.OfPoint(bb.Max).Z;
                    z0 = UnitConv.ToMm(Math.Min(za, zb));
                    z1 = UnitConv.ToMm(Math.Max(za, zb));
                }

                s.InnerWalls.Add(new InnerWallInfo
                {
                    Id = w.Id.GetIdValue(),
                    Source = src.Name,
                    X1 = UnitConv.ToMm(a.X), Y1 = UnitConv.ToMm(a.Y),
                    X2 = UnitConv.ToMm(b.X), Y2 = UnitConv.ToMm(b.Y),
                    ThicknessMm = UnitConv.ToMm(w.Width),
                    BottomZ = z0,
                    TopZ = z1
                });
            }
        }

        private void CollectAccessories(SourceContext src, BoundingBoxIntersectsFilter box, ShaftInfo s)
        {
            IList<Element> list;
            try
            {
                list = new FilteredElementCollector(src.Doc)
                    .WherePasses(new ElementMulticategoryFilter(AccessoryCats))
                    .WhereElementIsNotElementType()
                    .WherePasses(box)
                    .ToElements();
            }
            catch (Exception) { return; }

            foreach (Element e in list)
            {
                var fi = e as FamilyInstance;
                Element type = e.Document.GetElement(e.GetTypeId());
                string name = fi != null && fi.Symbol != null
                    ? fi.Symbol.FamilyName + " " + fi.Symbol.Name
                    : (type != null ? type.Name : e.Name);

                bool isDamper = RuleConfig.MatchesAny(name, _cfg.FireDamperPatterns) ||
                                ParamReader.ReadYesNo(e, type, _cfg.FireDamperParam) == true;
                if (!isDamper && IsCat(e, BuiltInCategory.OST_GenericModel)) continue; // 一般模型只收防火閘門

                XYZ p = null;
                var lp = e.Location as LocationPoint;
                if (lp != null) p = lp.Point;
                else
                {
                    BoundingBoxXYZ bb = null;
                    try { bb = e.get_BoundingBox(null); } catch (Exception) { }
                    if (bb != null) p = (bb.Min + bb.Max) / 2;
                }
                if (p == null) continue;
                XYZ h = src.ToHost.OfPoint(p);

                s.Accessories.Add(new AccessoryInfo
                {
                    Id = e.Id.GetIdValue(),
                    Source = src.Name,
                    Name = name,
                    X = UnitConv.ToMm(h.X), Y = UnitConv.ToMm(h.Y), Z = UnitConv.ToMm(h.Z),
                    IsFireDamper = isDamper
                });
            }
        }
    }
}
