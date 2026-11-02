using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LebrCarbon;

namespace LebrCarbon.Revit
{
    /// <summary>
    /// 模組三：從目前的 Revit 模型擷取數量，輸出計畫書第十一章的數量格式（QuantityInput）。
    /// 構件依類型上的 LEBR_Code 加總面積；整棟幾何由樓層、房間/樓板、格線與樓板外框計算，
    /// 專案資訊有填 LEBR_AFu、LEBR_Sp 等選填參數時，以填的值為準。只讀取，不修改模型。
    /// </summary>
    public class RevitQuantityProvider
    {
        private const double MinSpanM = 0.5;

        private readonly Document _doc;
        private readonly LebrDatabase _db;
        private readonly Dictionary<string, ComponentQuantity> _components = new Dictionary<string, ComponentQuantity>(StringComparer.OrdinalIgnoreCase);
        private readonly List<UnmappedElement> _unmapped = new List<UnmappedElement>();
        private readonly List<string> _notes = new List<string>();

        public RevitQuantityProvider(Document doc, LebrDatabase db)
        {
            _doc = doc;
            _db = db;
        }

        public QuantityInput Read()
        {
            _components.Clear();
            _unmapped.Clear();
            _notes.Clear();

            if (!HasParameters())
                _notes.Add("這個專案還沒有 LEBR 參數，請先按「LEBR → 專案設定」（會自動建立參數），再填構件代號與專案資訊。");

            ReadWalls();
            ReadWindows();
            ReadCurtainPanels();
            ReadFloors();

            return new QuantityInput
            {
                Source = "目前模型：" + _doc.Title,
                Building = ReadBuilding(),
                Project = ReadProject(),
                Components = _components.Values.OrderBy(c => c.Code).ToList(),
                Unmapped = _unmapped,
                Notes = _notes,
            };
        }

        // ------------------------------------------------------------ 構件

        private void ReadWalls()
        {
            var skippedStructural = 0;
            foreach (var wall in new FilteredElementCollector(_doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
            {
                var type = wall.WallType;
                if (type == null || type.Kind == WallKind.Curtain || Excluded(wall)) continue;
                var function = type.Function;
                if (function != WallFunction.Exterior && function != WallFunction.Interior) continue;

                var area = SquareMeters(wall.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED));
                if (area <= 0) continue;
                var code = Text(type, LebrParameters.Code);
                var body = Text(type, LebrParameters.CodeBody);
                if (code == null && body == null)
                {
                    var structural = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
                    if (function == WallFunction.Interior && structural)
                    {
                        skippedStructural++; // 結構牆屬於主結構，不算內隔間
                        continue;
                    }
                    Unmapped(wall, type, function == WallFunction.Exterior
                        ? "外牆類型沒有填 LEBR_Code（外裝）或 LEBR_Code_Body（本體）"
                        : "內牆類型沒有填 LEBR_Code；不計入的牆請勾選 LEBR_Exclude");
                    continue;
                }
                if (code != null) Add(code, area, wall, type);
                if (body != null) Add(body, area, wall, type);
            }
            if (skippedStructural > 0)
                _notes.Add(skippedStructural + " 面結構性內牆未填代號，視為主結構，不計入內隔間。");
        }

        private void ReadWindows()
        {
            foreach (var fi in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Windows)
                         .WhereElementIsNotElementType().OfType<FamilyInstance>())
            {
                if (Excluded(fi)) continue;
                var type = fi.Symbol;
                var code = Text(type, LebrParameters.Code);
                if (code == null)
                {
                    Unmapped(fi, type, "窗類型沒有填 LEBR_Code");
                    continue;
                }
                var w = Length(fi, type, BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, "寬度", "Width");
                var h = Length(fi, type, BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, "高度", "Height");
                if (w <= 0 || h <= 0)
                {
                    Unmapped(fi, type, "讀不到窗的寬度或高度");
                    continue;
                }
                Add(code, w * h, fi, type);
            }
        }

        private void ReadCurtainPanels()
        {
            foreach (var e in new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_CurtainWallPanels).WhereElementIsNotElementType())
            {
                if (Excluded(e)) continue;
                var type = _doc.GetElement(e.GetTypeId()) as ElementType;
                var area = SquareMeters(e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED));
                if (area <= 0)
                    area = Meters(e.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_WIDTH)) * Meters(e.get_Parameter(BuiltInParameter.CURTAIN_WALL_PANELS_HEIGHT));
                if (area <= 0) continue;
                var code = Text(type, LebrParameters.Code);
                if (code == null)
                {
                    Unmapped(e, type, "帷幕嵌板類型沒有填 LEBR_Code（透光填 CWW-…，不透光填 CW-…）");
                    continue;
                }
                Add(code, area, e, type);
            }
        }

        private void ReadFloors()
        {
            var uncoded = 0;
            foreach (var floor in new FilteredElementCollector(_doc).OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>())
            {
                if (Excluded(floor)) continue;
                var code = Text(floor.FloorType, LebrParameters.Code);
                if (code == null)
                {
                    uncoded++; // 沒填代號的樓板視為結構樓板，屬主結構
                    continue;
                }
                var area = SquareMeters(floor.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED));
                if (area > 0) Add(code, area, floor, floor.FloorType);
            }
            if (uncoded > 0)
                _notes.Add(uncoded + " 片樓板未填代號，視為結構樓板（屬主結構），不計入地坪。地坪面層請在樓板類型填 F-… 或 P-…。");
        }

        private void Add(string code, double areaM2, Element e, ElementType type)
        {
            ComponentQuantity c;
            if (!_components.TryGetValue(code, out c))
            {
                c = new ComponentQuantity { Code = code, Name = type != null ? type.Name : code, ElementIds = new List<long>() };
                _components[code] = c;
            }
            else if (type != null && !c.Name.Contains(type.Name))
            {
                c.Name = c.Name.EndsWith("…") ? c.Name : c.Name + "、" + type.Name;
                if (c.Name.Length > 60) c.Name = c.Name.Substring(0, 58) + "…";
            }
            c.AreaM2 += areaM2;
            c.ElementIds.Add(e.Id.Value);
        }

        private void Unmapped(Element e, ElementType type, string reason)
        {
            _unmapped.Add(new UnmappedElement
            {
                ElementId = e.Id.Value,
                Category = e.Category != null ? e.Category.Name : "",
                TypeName = type != null ? type.Name : e.Name,
                Reason = reason,
            });
        }

        // ------------------------------------------------------------ 專案參數（專案資訊）

        private ProjectSettings ReadProject()
        {
            var info = _doc.ProjectInformation;
            return new ProjectSettings
            {
                WearClass = WearClass(Text(info, "LEBR_WearClass")),
                Structure = Structure(Text(info, "LEBR_Structure")),
                UseType = UseType(Text(info, "LEBR_UseType")),
                I = Number(info, "LEBR_I"),
                SaD = Number(info, "LEBR_SaD"),
                Fu = Number(info, "LEBR_Fu"),
                L = Number(info, "LEBR_L"),
                D0 = Number(info, "LEBR_D0"),
                Rs = Number(info, "LEBR_Rs"),
                CSER = Number(info, "LEBR_CSER") ?? 0,
                LL = Number(info, "LEBR_LL") ?? 0,
                StructureCFs = Number(info, "LEBR_CFs"),
                StructureEstimateJson = Text(info, "LEBR_EstimateJson"),
            };
        }

        private static string WearClass(string v)
        {
            if (v == null) return null;
            if (v.StartsWith("H", StringComparison.OrdinalIgnoreCase) || v.StartsWith("高")) return "H";
            if (v.StartsWith("M", StringComparison.OrdinalIgnoreCase) || v.StartsWith("中")) return "M";
            if (v.StartsWith("L", StringComparison.OrdinalIgnoreCase) || v.StartsWith("低")) return "L";
            return v;
        }

        private static string Structure(string v)
        {
            if (v == null) return null;
            var s = v.ToUpperInvariant();
            if (s.StartsWith("SRC")) return "SRC";
            if (s.StartsWith("RC") || v.StartsWith("鋼筋混凝土")) return "RC";
            if (v.Contains("輕鋼") || s.Contains("LIGHT")) return "light_steel";
            if (v.Contains("木") || s.StartsWith("WOOD")) return "wood";
            if (v.Contains("磚") || v.Contains("石") || s.StartsWith("MASONRY")) return "masonry";
            if (s == "S" || v.Contains("鋼構") || v.Contains("鋼骨")) return "S";
            return v;
        }

        private static string UseType(string v)
        {
            if (v == null) return null;
            if (v.Contains("住") || v.Contains("旅館") || v.Contains("醫院") || v.StartsWith("res", StringComparison.OrdinalIgnoreCase)) return "residential";
            if (v.Contains("廠") || v.StartsWith("fac", StringComparison.OrdinalIgnoreCase)) return "factory";
            return "other";
        }

        // ------------------------------------------------------------ 整棟幾何（主結構用）

        private BuildingGeometry ReadBuilding()
        {
            var info = _doc.ProjectInformation;
            var levels = new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>().ToDictionary(l => l.Id.Value);

            // 樓地板面積：有房間就用房間面積，沒有就用樓板面積
            var areaByLevel = new Dictionary<long, double>();
            var rooms = new FilteredElementCollector(_doc).OfCategory(BuiltInCategory.OST_Rooms).WhereElementIsNotElementType()
                .OfType<SpatialElement>().Where(r => r.Area > 0 && r.LevelId != ElementId.InvalidElementId).ToList();
            if (rooms.Count > 0)
            {
                foreach (var r in rooms) Accumulate(areaByLevel, r.LevelId.Value, SquareMeters(r.Area));
                _notes.Add("樓地板面積依房間面積加總（" + rooms.Count + " 個房間）。");
            }
            else
            {
                foreach (var f in new FilteredElementCollector(_doc).OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>())
                    if (f.LevelId != ElementId.InvalidElementId && !Excluded(f))
                        Accumulate(areaByLevel, f.LevelId.Value, SquareMeters(f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)));
                _notes.Add("模型沒有房間，樓地板面積依樓板面積加總（可能含面層重複，建議放房間或填 LEBR_AFu）。");
            }

            var used = areaByLevel.Where(kv => kv.Value > 0 && levels.ContainsKey(kv.Key))
                .Select(kv => new { Level = levels[kv.Key], Area = kv.Value, Elevation = Meters(levels[kv.Key].ProjectElevation) })
                .OrderBy(x => x.Elevation).ToList();
            var above = used.Where(x => x.Elevation >= -0.01).ToList();
            var below = used.Where(x => x.Elevation < -0.01).ToList();
            var topElevation = levels.Values.Max(l => Meters(l.ProjectElevation));
            if (rooms.Count == 0 && above.Count > 1 && above[above.Count - 1].Elevation >= topElevation - 0.01)
            {
                // 用樓板面積時，模型最高樓層上的樓板是屋頂板，不是一層樓（已用 LEBR_Exclude 排除者不受影響）
                var roof = above[above.Count - 1];
                above.RemoveAt(above.Count - 1);
                _notes.Add("最高的 " + roof.Level.Name + " 樓板視為屋頂板，不計入樓層數與樓地板面積。");
            }

            var b = new BuildingGeometry
            {
                AFu = Number(info, "LEBR_AFu") ?? (above.Count > 0 ? above.Sum(x => x.Area) : (double?)null),
                AFb = Number(info, "LEBR_AFb") ?? below.Sum(x => x.Area),
                S = Number(info, "LEBR_S") ?? (above.Count > 0 ? above.Count : (double?)null),
                Sb = Number(info, "LEBR_Sb") ?? below.Count,
                BH = Number(info, "LEBR_BH") ?? (above.Count >= 2 ? above[1].Elevation - above[0].Elevation : (double?)null),
                Sp = Number(info, "LEBR_Sp") ?? SpanFactor(),
                F = Number(info, "LEBR_F") ?? ShapeFactor(above.Select(x => x.Level).ToList()),
            };
            if (above.Count > 0)
                _notes.Add(string.Format("地上 {0} 層（{1}）、地下 {2} 層，地上樓地板 {3:N0} m²。",
                    above.Count, string.Join("、", above.Select(x => x.Level.Name).Take(6)) + (above.Count > 6 ? "…" : ""), below.Count, above.Sum(x => x.Area)));
            return b;
        }

        /// <summary>跨距變化係數 Sp（式 a～c），由格線間距計算。只處理直線格線。</summary>
        private double? SpanFactor()
        {
            var xs = new List<double>();
            var ys = new List<double>();
            foreach (var g in new FilteredElementCollector(_doc).OfClass(typeof(Grid)).Cast<Grid>())
            {
                var line = g.Curve as Line;
                if (line == null) continue;
                var mid = line.Evaluate(0.5, true);
                if (Math.Abs(line.Direction.Y) > Math.Abs(line.Direction.X)) xs.Add(Meters(mid.X));
                else ys.Add(Meters(mid.Y));
            }
            var ax = Axis(xs);
            var ay = Axis(ys);
            if (ax == null && ay == null)
            {
                _notes.Add("模型沒有格線，無法計算跨距變化係數 Sp，請在專案資訊填 LEBR_Sp。");
                return null;
            }
            double bx = ax?.Item2 ?? 0, by = ay?.Item2 ?? 0;
            var sp = ((ax?.Item1 ?? 0) * bx + (ay?.Item1 ?? 0) * by) / (bx + by);
            _notes.Add(string.Format("跨距變化係數 Sp = {0:0.00}（由 {1} 條 X 向、{2} 條 Y 向格線計算）。", Math.Max(1.0, sp), xs.Count, ys.Count));
            return Math.Max(1.0, sp);
        }

        /// <summary>單軸跨距比與總跨距：a = max(最大/平均, 平均/最小)。</summary>
        private static Tuple<double, double> Axis(List<double> positions)
        {
            var p = positions.OrderBy(v => v).ToList();
            var spans = new List<double>();
            for (var i = 1; i < p.Count; i++)
                if (p[i] - p[i - 1] >= MinSpanM) spans.Add(p[i] - p[i - 1]);
            if (spans.Count == 0) return null;
            var avg = spans.Average();
            var a = Math.Max(spans.Max() / avg, avg / spans.Min());
            return Tuple.Create(a, spans.Sum());
        }

        /// <summary>形狀係數 F = f1 × f2（× f3）：各地上層取最大樓板的外框，依面積加權平均。f3 出挑暫不自動判斷。</summary>
        private double? ShapeFactor(List<Level> aboveLevels)
        {
            var ids = new HashSet<long>(aboveLevels.Select(l => l.Id.Value));
            var floorsByLevel = new FilteredElementCollector(_doc).OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>()
                .Where(f => ids.Contains(f.LevelId.Value) && !Excluded(f))
                .GroupBy(f => f.LevelId.Value);
            double weighted = 0, total = 0;
            foreach (var g in floorsByLevel)
            {
                var floor = g.OrderByDescending(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0).First();
                double perimeter, area, aspect;
                if (!Outline(floor, out perimeter, out area, out aspect) || area <= 0) continue;
                var f12 = _db.ShapeFactorF1(perimeter, area) * _db.AspectFactorF2(aspect);
                weighted += f12 * area;
                total += area;
            }
            if (total <= 0)
            {
                _notes.Add("讀不到地上層樓板外框，無法計算形狀係數 F，請在專案資訊填 LEBR_F。");
                return null;
            }
            var f = weighted / total;
            _notes.Add(string.Format("形狀係數 F = {0:0.000}（f1 × f2 由各層最大樓板外框估算；出挑修正 f3 以 1.0 計，有大出挑請填 LEBR_F）。", f));
            return f;
        }

        /// <summary>樓板頂面外框的周長 P（m）、面積 A（m²）與外接矩形長寬比。</summary>
        private static bool Outline(Floor floor, out double perimeter, out double area, out double aspect)
        {
            perimeter = area = aspect = 0;
            try
            {
                foreach (var r in HostObjectUtils.GetTopFaces(floor))
                {
                    var face = floor.GetGeometryObjectFromReference(r) as Face;
                    if (face == null) continue;
                    var loop = face.GetEdgesAsCurveLoops().OrderByDescending(l => l.GetExactLength()).FirstOrDefault();
                    if (loop == null) continue;
                    var pts = loop.SelectMany(c => c.Tessellate()).ToList();
                    var dx = Meters(pts.Max(p => p.X) - pts.Min(p => p.X));
                    var dy = Meters(pts.Max(p => p.Y) - pts.Min(p => p.Y));
                    var a = SquareMeters(face.Area);
                    if (a <= area) continue;
                    area = a;
                    perimeter = Meters(loop.GetExactLength());
                    aspect = Math.Min(dx, dy) > 0 ? Math.Max(dx, dy) / Math.Min(dx, dy) : 1;
                }
                return area > 0;
            }
            catch (Autodesk.Revit.Exceptions.ApplicationException)
            {
                return false;
            }
        }

        // ------------------------------------------------------------ 參數讀取工具

        private bool HasParameters()
        {
            return _doc.ProjectInformation.LookupParameter("LEBR_WearClass") != null;
        }

        private static bool Excluded(Element e)
        {
            var p = e.LookupParameter(LebrParameters.Exclude);
            return p != null && p.HasValue && p.AsInteger() == 1;
        }

        private static string Text(Element e, string name)
        {
            var p = e?.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            var v = p.StorageType == StorageType.String ? p.AsString() : p.AsValueString();
            return string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        }

        /// <summary>數字參數；沒填或填 0 都視為未填（CSER、LL 未填時由呼叫端補 0）。</summary>
        private static double? Number(Element e, string name)
        {
            var p = e?.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            double v;
            if (p.StorageType == StorageType.Double) v = p.AsDouble();
            else if (p.StorageType == StorageType.Integer) v = p.AsInteger();
            else if (p.StorageType != StorageType.String || !double.TryParse(p.AsString(), out v)) return null;
            return Math.Abs(v) > 1e-12 ? v : (double?)null;
        }

        /// <summary>窗寬高：先找實例參數，再找類型參數，最後依名稱。</summary>
        private static double Length(Element instance, Element type, BuiltInParameter instanceParam, BuiltInParameter typeParam, params string[] names)
        {
            var p = instance.get_Parameter(instanceParam) ?? type?.get_Parameter(typeParam);
            if (p == null || !p.HasValue || p.AsDouble() <= 0)
            {
                p = null;
                foreach (var n in names)
                {
                    p = instance.LookupParameter(n) ?? type?.LookupParameter(n);
                    if (p != null && p.StorageType == StorageType.Double && p.AsDouble() > 0) break;
                    p = null;
                }
            }
            return Meters(p);
        }

        private static void Accumulate(Dictionary<long, double> map, long key, double value)
        {
            double v;
            map[key] = (map.TryGetValue(key, out v) ? v : 0) + value;
        }

        private static double Meters(Parameter p)
        {
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? Meters(p.AsDouble()) : 0;
        }

        private static double SquareMeters(Parameter p)
        {
            return p != null && p.HasValue && p.StorageType == StorageType.Double ? SquareMeters(p.AsDouble()) : 0;
        }

        private static double Meters(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        }

        private static double SquareMeters(double squareFeet)
        {
            return UnitUtils.ConvertFromInternalUnits(squareFeet, UnitTypeId.SquareMeters);
        }
    }
}
