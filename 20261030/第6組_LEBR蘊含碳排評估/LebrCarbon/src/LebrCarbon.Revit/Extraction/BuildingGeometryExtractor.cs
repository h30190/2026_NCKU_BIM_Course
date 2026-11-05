using Autodesk.Revit.DB;

namespace LebrCarbon.Revit.Extraction;

/// <summary>每個樓層的樓地板面積，供計算 S、Sb、AFu、AFb、BH。</summary>
public sealed record LevelInfo(long Id, string Name, double ElevationM, double FloorAreaM2);

/// <summary>標準層平面形狀（表 5、表 6 所需）。</summary>
public sealed record ShapeInfo(string LevelName, double AreaM2, double PerimeterM, double Aspect, int Parts);

/// <summary>從模型擷取的建築幾何，全部為公制。</summary>
public sealed class BuildingGeometry
{
    public List<LevelInfo> Levels { get; } = new();
    public List<double> XSpans { get; } = new();
    public List<double> YSpans { get; } = new();
    public ShapeInfo? Shape { get; set; }
    public List<string> Warnings { get; } = new();
}

/// <summary>
/// 唯讀擷取建築幾何（計畫書第 5 節步驟 1–2 的 LEBR 版）：
/// 樓層與樓地板面積、軸線跨距、標準層平面周長與長寬比。
/// </summary>
public static class BuildingGeometryExtractor
{
    private const double FtToM = 0.3048;
    private const double Ft2ToM2 = 0.09290304;

    public static BuildingGeometry Extract(Document doc)
    {
        var g = new BuildingGeometry();
        var floorsByLevel = CollectFloorsByLevel(doc);

        foreach (var lv in new FilteredElementCollector(doc).OfClass(typeof(Level)).Cast<Level>().OrderBy(l => l.Elevation))
        {
            var area = floorsByLevel.TryGetValue(lv.Id, out var fs)
                ? fs.Sum(f => f.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0) * Ft2ToM2
                : 0;
            g.Levels.Add(new LevelInfo(lv.Id.Value, lv.Name, lv.Elevation * FtToM, area));
        }

        ExtractSpans(doc, g);
        return g;
    }

    /// <summary>
    /// 每層的樓板：若該層有結構樓板，只取結構樓板，避免與裝修面層重複計算面積；
    /// LEBR_Scope = 排除 者不計。
    /// </summary>
    private static Dictionary<ElementId, List<Floor>> CollectFloorsByLevel(Document doc)
    {
        var all = new FilteredElementCollector(doc).OfClass(typeof(Floor)).Cast<Floor>()
            .Where(f => f.LookupParameter(StructuralQuantityExtractor.ParamScope)?.AsString() is not ("排除" or "Excluded"))
            .ToList();

        return all.GroupBy(f => f.LevelId).ToDictionary(
            grp => grp.Key,
            grp =>
            {
                var structural = grp.Where(IsStructural).ToList();
                return structural.Count > 0 ? structural : grp.ToList();
            });
    }

    private static bool IsStructural(Floor f) =>
        f.get_Parameter(BuiltInParameter.FLOOR_PARAM_IS_STRUCTURAL)?.AsInteger() == 1;

    /// <summary>
    /// 軸線分兩組：與第一條直線軸線平行者、垂直者；各組依法向位置排序後相鄰差值即為跨距。
    /// 可處理旋轉的軸網；曲線軸線略過。
    /// </summary>
    private static void ExtractSpans(Document doc, BuildingGeometry g)
    {
        var lines = new FilteredElementCollector(doc).OfClass(typeof(Grid)).Cast<Grid>()
            .Where(x => !x.IsCurved && x.Curve is Line)
            .Select(x => (Line)x.Curve)
            .ToList();

        if (lines.Count < 2)
        {
            g.Warnings.Add("模型中直線軸線少於 2 條，跨距變化係數 Sp 以 1.0 計，請自行確認");
            return;
        }

        // 以 X 軸最接近的方向當作「X 向跨距」的量測方向
        var u = lines.Select(l => l.Direction).OrderBy(d => Math.Abs(d.Y)).First();
        u = new XYZ(Math.Abs(u.X), u.X >= 0 ? u.Y : -u.Y, 0).Normalize();
        var v = new XYZ(-u.Y, u.X, 0);

        var xPos = new List<double>(); // 垂直於 u 的軸線（量 X 向跨距）
        var yPos = new List<double>();
        foreach (var l in lines)
        {
            var d = l.Direction;
            var p = l.GetEndPoint(0);
            if (Math.Abs(d.DotProduct(v)) > 0.98) xPos.Add(p.DotProduct(u) * FtToM);
            else if (Math.Abs(d.DotProduct(u)) > 0.98) yPos.Add(p.DotProduct(v) * FtToM);
        }

        g.XSpans.AddRange(Spans(xPos));
        g.YSpans.AddRange(Spans(yPos));
        if (lines.Count != xPos.Count + yPos.Count)
            g.Warnings.Add($"有 {lines.Count - xPos.Count - yPos.Count} 條斜向軸線未納入跨距計算");
    }

    private static IEnumerable<double> Spans(List<double> positions)
    {
        var sorted = positions.OrderBy(x => x).ToList();
        var dedup = new List<double>();
        foreach (var x in sorted)
            if (dedup.Count == 0 || x - dedup[^1] > 0.05) dedup.Add(x); // 5cm 內視為同一條（例如重複的軸線）
        for (var i = 1; i < dedup.Count; i++) yield return Math.Round(dedup[i] - dedup[i - 1], 3);
    }

    /// <summary>
    /// 計算指定樓層的平面形狀：合併該層樓板實體，取向上的頂面，外框周長總和與面積總和；
    /// 長寬比取最大一塊平面的最小外接矩形。
    /// </summary>
    public static ShapeInfo? ExtractShape(Document doc, long levelId, BuildingGeometry g)
    {
        var floors = CollectFloorsByLevel(doc).TryGetValue(new ElementId(levelId), out var fs) ? fs : null;
        var levelName = doc.GetElement(new ElementId(levelId))?.Name ?? "";
        if (floors is null || floors.Count == 0)
        {
            g.Warnings.Add($"樓層 {levelName} 沒有樓板，無法計算平面形狀");
            return null;
        }

        var solids = floors.SelectMany(SolidsOf).Where(s => s.Volume > 1e-6).ToList();
        if (solids.Count == 0) return null;

        Solid union = solids[0];
        for (var i = 1; i < solids.Count; i++)
        {
            try
            {
                union = BooleanOperationsUtils.ExecuteBooleanOperation(union, solids[i], BooleanOperationsType.Union);
            }
            catch (Exception)
            {
                g.Warnings.Add("部分樓板無法合併，平面形狀可能偏差，請檢查周長與面積");
            }
        }

        var tops = union.Faces.OfType<PlanarFace>().Where(f => f.FaceNormal.Z > 0.99).ToList();
        if (tops.Count == 0) return null;

        // 只取最高的頂面群（同層不同厚度的樓板頂面高度相同；降版處較低的面略過）
        var maxZ = tops.Max(f => f.Origin.Z);
        tops = tops.Where(f => Math.Abs(f.Origin.Z - maxZ) < 0.5 / 0.3048).ToList();

        double area = 0, perimeter = 0;
        List<XYZ>? largestOutline = null;
        double largestArea = 0;
        foreach (var face in tops)
        {
            area += face.Area * Ft2ToM2;
            var outer = OuterLoop(face);
            perimeter += outer.Sum(e => e.AsCurve().Length) * FtToM;
            if (face.Area > largestArea)
            {
                largestArea = face.Area;
                largestOutline = outer.SelectMany(e => e.Tessellate()).ToList();
            }
        }

        var aspect = largestOutline is null ? 1.0 : MinRectAspect(largestOutline);
        return new ShapeInfo(levelName, area, perimeter, aspect, tops.Count);
    }

    private static IEnumerable<Solid> SolidsOf(Element e)
    {
        var geo = e.get_Geometry(new Options { ComputeReferences = false, DetailLevel = ViewDetailLevel.Coarse });
        if (geo is null) yield break;
        foreach (var o in geo)
        {
            if (o is Solid s) yield return s;
            else if (o is GeometryInstance gi)
                foreach (var s2 in gi.GetInstanceGeometry().OfType<Solid>()) yield return s2;
        }
    }

    /// <summary>外框 = 周長最長的那一圈（其餘為開口，例如管道間、挑空）。</summary>
    private static List<Edge> OuterLoop(PlanarFace face)
    {
        List<Edge> best = new();
        double bestLen = -1;
        foreach (EdgeArray loop in face.EdgeLoops)
        {
            var edges = loop.Cast<Edge>().ToList();
            var len = edges.Sum(e => e.AsCurve().Length);
            if (len > bestLen) { bestLen = len; best = edges; }
        }
        return best;
    }

    /// <summary>以各邊方向旋轉求最小面積外接矩形，回傳長邊/短邊（表 6 平面長寬比 b）。</summary>
    private static double MinRectAspect(List<XYZ> pts)
    {
        if (pts.Count < 3) return 1.0;
        double bestArea = double.MaxValue, bestAspect = 1.0;
        for (var i = 0; i < pts.Count; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            var d = new XYZ(b.X - a.X, b.Y - a.Y, 0);
            if (d.GetLength() < 1e-6) continue;
            var ux = d.Normalize();
            var uy = new XYZ(-ux.Y, ux.X, 0);
            double minX = double.MaxValue, maxX = double.MinValue, minY = double.MaxValue, maxY = double.MinValue;
            foreach (var p in pts)
            {
                var px = p.X * ux.X + p.Y * ux.Y;
                var py = p.X * uy.X + p.Y * uy.Y;
                minX = Math.Min(minX, px); maxX = Math.Max(maxX, px);
                minY = Math.Min(minY, py); maxY = Math.Max(maxY, py);
            }
            var w = maxX - minX;
            var h = maxY - minY;
            if (w * h < bestArea && Math.Min(w, h) > 1e-6)
            {
                bestArea = w * h;
                bestAspect = Math.Max(w, h) / Math.Min(w, h);
            }
        }
        return bestAspect;
    }
}
