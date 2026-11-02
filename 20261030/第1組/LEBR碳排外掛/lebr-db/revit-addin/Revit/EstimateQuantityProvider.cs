using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using Autodesk.Revit.DB.Structure;
using LebrCarbon;

namespace LebrCarbon.Revit
{
    /// <summary>
    /// 結構碳排估算的 Revit 數量擷取（只讀，不修改模型）。依來源品類與樓層彙總，供 EstimateQuantities.ToItems 使用。
    /// 混凝土：結構柱、結構梁、所有樓板、混凝土類結構牆（結構 = 是）。
    /// 鋼筋：柱、梁的鋼筋（Rebar）與樓板鋼筋（區域配筋、路徑配筋的系統內鋼筋），皆依主體樓層歸屬。
    /// </summary>
    public class EstimateQuantityProvider
    {
        /// <summary>鋼筋密度（kg/m³）。已以模型驗證：鋼筋體積 3572.66 cm³ × 7850 = 28.04 kg，與「鋼筋質量」相符。</summary>
        private const double SteelDensityKgPerM3 = 7850;

        private readonly Document _doc;
        private readonly Dictionary<string, EstimateQuantityRow> _rows = new Dictionary<string, EstimateQuantityRow>(StringComparer.Ordinal);
        private readonly List<string> _notes = new List<string>();

        public EstimateQuantityProvider(Document doc)
        {
            _doc = doc;
        }

        public List<EstimateQuantityRow> Read(out List<string> notes)
        {
            _rows.Clear();
            _notes.Clear();

            ReadColumns();
            ReadFraming();
            ReadFloors();
            ReadWalls();
            ReadRebar();

            notes = new List<string>(_notes);
            return _rows.Values.OrderBy(r => r.Source).ThenBy(r => r.ElevationM).ToList();
        }

        // ------------------------------------------------------------ 混凝土

        private void ReadColumns()
        {
            var skipped = 0;
            foreach (var e in Collect(BuiltInCategory.OST_StructuralColumns))
            {
                var level = LevelOf(e, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                var volume = VolumeM3(e);
                if (level == null || volume <= 0) { skipped++; continue; }
                Add("StructuralColumns", level, volume, "m3", false);
            }
            if (skipped > 0) _notes.Add("結構柱 " + skipped + " 根沒有樓層或體積，未計入");
        }

        private void ReadFraming()
        {
            var noLevel = 0;
            var noVolume = 0;
            foreach (var e in Collect(BuiltInCategory.OST_StructuralFraming))
            {
                var level = LevelOf(e, BuiltInParameter.FAMILY_LEVEL_PARAM, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM);
                if (level == null) { noLevel++; continue; }
                var volume = VolumeM3(e);
                if (volume <= 0) { noVolume++; continue; }
                Add("StructuralFraming", level, volume, "m3", false);
            }
            if (noLevel > 0) _notes.Add("結構梁 " + noLevel + " 支讀不到樓層，未計入");
            if (noVolume > 0) _notes.Add("結構梁 " + noVolume + " 支讀不到體積，未計入");
        }

        /// <summary>所有樓板（不限結構樓板），依 LevelId 歸屬樓層。</summary>
        private void ReadFloors()
        {
            var skipped = 0;
            foreach (var f in new FilteredElementCollector(_doc).OfClass(typeof(Floor)).WhereElementIsNotElementType().Cast<Floor>())
            {
                var level = _doc.GetElement(f.LevelId) as Level;
                var volume = VolumeM3(f);
                if (level == null || volume <= 0) { skipped++; continue; }
                Add("Floors", level, volume, "m3", false);
            }
            if (skipped > 0) _notes.Add("樓板 " + skipped + " 片沒有樓層或體積，未計入");
        }

        private void ReadWalls()
        {
            var skipped = 0;
            foreach (var w in new FilteredElementCollector(_doc).OfClass(typeof(Wall)).WhereElementIsNotElementType().Cast<Wall>())
            {
                if (w.WallType == null || w.WallType.Kind == WallKind.Curtain) continue;
                var structural = w.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT)?.AsInteger() == 1;
                if (!structural || !IsConcreteWall(w)) continue;

                var level = LevelOf(w, BuiltInParameter.WALL_BASE_CONSTRAINT);
                var volume = VolumeM3(w);
                if (level == null || volume <= 0) { skipped++; continue; }
                Add("Walls", level, volume, "m3", false);
            }
            if (skipped > 0) _notes.Add("混凝土類結構牆 " + skipped + " 面沒有樓層或體積，未計入");
        }

        /// <summary>混凝土類判定：牆的任一層材質名稱含 Concrete 或「混凝土」。</summary>
        private bool IsConcreteWall(Wall w)
        {
            var cs = w.WallType.GetCompoundStructure();
            if (cs == null) return false;
            foreach (var layer in cs.GetLayers())
            {
                var material = _doc.GetElement(layer.MaterialId) as Material;
                if (material == null) continue;
                if (material.Name.IndexOf("Concrete", StringComparison.OrdinalIgnoreCase) >= 0 || material.Name.Contains("混凝土"))
                    return true;
            }
            return false;
        }

        // ------------------------------------------------------------ 鋼筋

        /// <summary>
        /// 結構鋼筋 = 柱、梁的鋼筋 + 樓板鋼筋。樓板的區域配筋與路徑配筋先處理，
        /// 其系統內的鋼筋記下來，避免在一般鋼筋中重複計入。
        /// </summary>
        private void ReadRebar()
        {
            var inSystems = new HashSet<long>();
            var skippedIds = new List<long>();

            foreach (var e in Collect(BuiltInCategory.OST_AreaRein).Concat(Collect(BuiltInCategory.OST_PathRein)))
            {
                var bars = BarsInSystem(e);
                foreach (var id in bars) inSystems.Add(id.Value);

                var host = HostOf(e);
                var level = host == null ? null : HostLevel(host);
                // 區域、路徑配筋裡的鋼筋是 RebarInSystem，不是 Rebar，要分開取體積
                var kg = bars.Select(id => _doc.GetElement(id)).Sum(BarMassKg);
                if (kg <= 0) kg = ParameterMassKg(e);
                if (level == null || kg <= 0) { skippedIds.Add(e.Id.Value); continue; }
                Add("Rebar", level, kg, "kg", true);
            }

            foreach (var rebar in Collect(BuiltInCategory.OST_Rebar).OfType<Rebar>())
            {
                if (inSystems.Contains(rebar.Id.Value)) continue;
                var host = _doc.GetElement(rebar.GetHostId());
                var level = host == null ? null : HostLevel(host);
                var kg = MassKg(rebar);
                if (level == null || kg <= 0) { skippedIds.Add(rebar.Id.Value); continue; }
                Add("Rebar", level, kg, "kg", true);
            }

            if (skippedIds.Count > 0)
                _notes.Add("結構鋼筋 " + skippedIds.Count + " 項沒有主體樓層或體積，未計入（ElementId："
                    + string.Join("、", skippedIds.Take(10)) + (skippedIds.Count > 10 ? " 等" : "") + "，可用「依 ID 選取」找到）");
        }

        private static ICollection<ElementId> BarsInSystem(Element e)
        {
            var area = e as AreaReinforcement;
            if (area != null) return area.GetRebarInSystemIds();
            var path = e as PathReinforcement;
            if (path != null) return path.GetRebarInSystemIds();
            return new List<ElementId>();
        }

        private Element HostOf(Element e)
        {
            ElementId hostId = null;
            var area = e as AreaReinforcement;
            if (area != null) hostId = area.GetHostId();
            var path = e as PathReinforcement;
            if (path != null) hostId = path.GetHostId();
            return hostId == null ? null : _doc.GetElement(hostId);
        }

        /// <summary>鋼筋質量（kg）＝ 鋼筋體積（m³）× 7850 kg/m³。體積已含整組數量。</summary>
        private static double MassKg(Rebar rebar)
        {
            return VolumeM3(rebar) * SteelDensityKgPerM3;
        }

        /// <summary>配筋系統內的單一鋼筋：一般鋼筋（Rebar）或系統內鋼筋（RebarInSystem）。</summary>
        private static double BarMassKg(Element e)
        {
            var rebar = e as Rebar;
            if (rebar != null) return MassKg(rebar);
            var inSystem = e as RebarInSystem;
            if (inSystem != null) return UnitUtils.ConvertFromInternalUnits(inSystem.Volume, UnitTypeId.CubicMeters) * SteelDensityKgPerM3;
            return 0;
        }

        /// <summary>退回方案：配筋元件本身的「鋼筋體積」參數（讀不到個別鋼筋時用）。</summary>
        private static double ParameterMassKg(Element e)
        {
            var p = e.get_Parameter(BuiltInParameter.REINFORCEMENT_VOLUME);
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double) return 0;
            return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.CubicMeters) * SteelDensityKgPerM3;
        }

        // ------------------------------------------------------------ 工具

        private IEnumerable<Element> Collect(BuiltInCategory category)
        {
            return new FilteredElementCollector(_doc).OfCategory(category).WhereElementIsNotElementType();
        }

        private void Add(string source, Level level, double quantity, string unit, bool isRebar)
        {
            var key = source + "|" + level.Name;
            EstimateQuantityRow row;
            if (!_rows.TryGetValue(key, out row))
            {
                row = new EstimateQuantityRow
                {
                    Source = source,
                    Level = level.Name,
                    ElevationM = Meters(level.ProjectElevation),
                    Unit = unit,
                    IsRebar = isRebar,
                };
                _rows[key] = row;
            }
            row.Quantity += quantity;
        }

        private Level HostLevel(Element host)
        {
            if (host is Floor) return _doc.GetElement(host.LevelId) as Level;
            if (host is Wall) return LevelOf(host, BuiltInParameter.WALL_BASE_CONSTRAINT);
            return LevelOf(host, BuiltInParameter.FAMILY_BASE_LEVEL_PARAM, BuiltInParameter.FAMILY_LEVEL_PARAM);
        }

        /// <summary>依參數順序取樓層；都取不到時退回元件的 LevelId（結構構架等族群可能沒有上述參數）。</summary>
        private Level LevelOf(Element e, params BuiltInParameter[] parameters)
        {
            foreach (var bip in parameters)
            {
                var p = e.get_Parameter(bip);
                if (p == null || p.StorageType != StorageType.ElementId) continue;
                var level = _doc.GetElement(p.AsElementId()) as Level;
                if (level != null) return level;
            }
            var byLevelId = _doc.GetElement(e.LevelId) as Level;
            return byLevelId ?? LevelNearestTop(e);
        }

        /// <summary>退回方案：取元件頂面高程最接近的樓層（結構梁等沒有樓層參數時使用）。</summary>
        private Level LevelNearestTop(Element e)
        {
            var box = e.get_BoundingBox(null);
            if (box == null) return null;
            var top = box.Max.Z;
            Level best = null;
            var bestDiff = double.MaxValue;
            foreach (var level in new FilteredElementCollector(_doc).OfClass(typeof(Level)).Cast<Level>())
            {
                var diff = Math.Abs(level.Elevation - top);
                if (diff < bestDiff) { bestDiff = diff; best = level; }
            }
            var toleranceFt = UnitUtils.ConvertToInternalUnits(1.0, UnitTypeId.Meters);
            return bestDiff <= toleranceFt ? best : null;
        }

        /// <summary>體積（m³）：依序用計算體積參數、名稱為 Volume 的參數、幾何實體。</summary>
        private static double VolumeM3(Element e)
        {
            var p = e.get_Parameter(BuiltInParameter.HOST_VOLUME_COMPUTED);
            if (p == null || !p.HasValue || p.StorageType != StorageType.Double || p.AsDouble() <= 0)
                p = e.LookupParameter("Volume");
            if (p != null && p.HasValue && p.StorageType == StorageType.Double && p.AsDouble() > 0)
                return UnitUtils.ConvertFromInternalUnits(p.AsDouble(), UnitTypeId.CubicMeters);

            var options = new Options { DetailLevel = ViewDetailLevel.Fine, IncludeNonVisibleObjects = true };
            var geometry = e.get_Geometry(options);
            var volume = SolidVolume(geometry);
            return UnitUtils.ConvertFromInternalUnits(volume, UnitTypeId.CubicMeters);
        }

        private static double SolidVolume(GeometryElement geometry)
        {
            if (geometry == null) return 0;
            double total = 0;
            foreach (var obj in geometry)
            {
                var solid = obj as Solid;
                if (solid != null)
                {
                    if (solid.Volume > 0) total += solid.Volume;
                    continue;
                }
                var instance = obj as GeometryInstance;
                if (instance != null) total += SolidVolume(instance.GetInstanceGeometry());
            }
            return total;
        }

        private static double VolumeM3(Rebar rebar)
        {
            return UnitUtils.ConvertFromInternalUnits(rebar.Volume, UnitTypeId.CubicMeters);
        }

        private static double Meters(double feet)
        {
            return UnitUtils.ConvertFromInternalUnits(feet, UnitTypeId.Meters);
        }
    }
}
