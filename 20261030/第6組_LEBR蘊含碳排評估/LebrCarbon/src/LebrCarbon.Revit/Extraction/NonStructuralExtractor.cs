using Autodesk.Revit.DB;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Revit.Extraction;

/// <summary>模型中一筆非主結構數量（已依 B-LCC ID 與類型彙總）。</summary>
public sealed record NsQuantity(string BlccId, string TypeName, string CategoryName, double AreaM2, int Count);

/// <summary>模型中一個有 LEBR_BlccId 的構件（未彙總）。</summary>
public sealed record NsElement(long ElementId, string TypeName, string CategoryName, IReadOnlyList<string> BlccIds, double AreaM2);

/// <summary>
/// 依類型參數 LEBR_BlccId 擷取非主結構構件面積（計畫書 F5）。
/// 一個類型可填多個 ID，以分號分隔，例如外牆類型填「CW-0;OW-4」代表 RC 外牆本體與外裝各計一次。
/// 地下層構件不納入評估範疇（手冊 3-2 節），以 groundElevationM 判斷。
/// </summary>
public static class NonStructuralExtractor
{
    public const string ParamBlccId = "LEBR_BlccId";
    private const double Ft2ToM2 = 0.09290304;

    public static readonly BuiltInCategory[] Categories =
    {
        BuiltInCategory.OST_Walls, BuiltInCategory.OST_Windows, BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_CurtainWallPanels, BuiltInCategory.OST_Doors,
    };

    public static List<NsQuantity> Extract(Document doc, double groundElevationM, List<string> warnings)
    {
        var acc = new Dictionary<(string Id, string Type, string Cat), (double Area, int Count)>();

        foreach (var e in ExtractElements(doc, groundElevationM, warnings))
        {
            foreach (var id in e.BlccIds)
            {
                var key = (id, e.TypeName, e.CategoryName);
                acc.TryGetValue(key, out var v);
                acc[key] = (v.Area + e.AreaM2, v.Count + 1);
            }
        }

        return acc.Select(kv => new NsQuantity(kv.Key.Id, kv.Key.Type, kv.Key.Cat, kv.Value.Area, kv.Value.Count))
            .OrderBy(q => q.BlccId).ToList();
    }

    /// <summary>
    /// 逐一列出有 LEBR_BlccId 的構件與面積（不彙總），供熱點著色等需要元件層級資料的功能使用。
    /// groundElevationM 傳 double.NegativeInfinity 代表地下層也納入。
    /// </summary>
    public static List<NsElement> ExtractElements(Document doc, double groundElevationM, List<string> warnings)
    {
        var list = new List<NsElement>();

        var elements = new FilteredElementCollector(doc)
            .WherePasses(new ElementMulticategoryFilter(Categories))
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var e in elements)
        {
            if (e.Category is null) continue;
            var type = doc.GetElement(e.GetTypeId());
            var ids = (type?.LookupParameter(ParamBlccId)?.AsString() ?? e.LookupParameter(ParamBlccId)?.AsString())
                ?.Split(';', ',', '；').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();
            if (ids is null || ids.Count == 0) continue;

            if (IsBelowGround(doc, e, groundElevationM)) continue;

            var area = AreaOf(e);
            if (area <= 0)
            {
                warnings.Add($"{e.Category.Name}「{type?.Name}」(ID {e.Id.Value}) 無法取得面積");
                continue;
            }

            list.Add(new NsElement(e.Id.Value, type?.Name ?? "", e.Category.Name, ids, area));
        }

        return list;
    }

    /// <summary>
    /// 牆、樓板、帷幕嵌板：Revit 計算面積（牆為單面）。
    /// 窗、門：寬 × 高（外框尺寸立面積，手冊式 11-2 注意事項 3）。
    /// 帷幕牆本身的牆元件不計，改由嵌板計算，避免重複。
    /// </summary>
    private static double AreaOf(Element e)
    {
        if (e is Wall w && w.CurtainGrid is not null) return 0;

        var cat = (BuiltInCategory)e.Category.Id.Value;
        if (cat is BuiltInCategory.OST_Windows or BuiltInCategory.OST_Doors)
        {
            var width = Dim(e,
                BuiltInParameter.WINDOW_WIDTH, BuiltInParameter.FAMILY_WIDTH_PARAM, BuiltInParameter.DOOR_WIDTH,
                BuiltInParameter.FAMILY_ROUGH_WIDTH_PARAM);
            if (width <= 0) width = DimByName(e, "寬度", "Width");

            var height = Dim(e,
                BuiltInParameter.WINDOW_HEIGHT, BuiltInParameter.FAMILY_HEIGHT_PARAM, BuiltInParameter.DOOR_HEIGHT,
                BuiltInParameter.FURNITURE_HEIGHT, BuiltInParameter.FAMILY_ROUGH_HEIGHT_PARAM);
            if (height <= 0)
            {
                // 窗頂高度 − 窗台高度
                var head = e.get_Parameter(BuiltInParameter.INSTANCE_HEAD_HEIGHT_PARAM)?.AsDouble() ?? 0;
                var sill = e.get_Parameter(BuiltInParameter.INSTANCE_SILL_HEIGHT_PARAM)?.AsDouble() ?? 0;
                if (head > sill) height = head - sill;
            }
            if (height <= 0) height = DimByName(e, "高度", "Height");
            return width * height * Ft2ToM2;
        }

        var a = e.get_Parameter(BuiltInParameter.HOST_AREA_COMPUTED)?.AsDouble() ?? 0;
        return a * Ft2ToM2;
    }

    /// <summary>依序找實例、類型上的尺寸參數。</summary>
    private static double Dim(Element e, params BuiltInParameter[] bips)
    {
        var type = e.Document.GetElement(e.GetTypeId());
        foreach (var bip in bips)
        {
            var p = e.get_Parameter(bip);
            if (p is { HasValue: true } && p.AsDouble() > 0) return p.AsDouble();
            p = type?.get_Parameter(bip);
            if (p is { HasValue: true } && p.AsDouble() > 0) return p.AsDouble();
        }
        return 0;
    }

    /// <summary>依參數名稱找長度參數（實例優先，再找類型）。</summary>
    private static double DimByName(Element e, params string[] names)
    {
        var type = e.Document.GetElement(e.GetTypeId());
        foreach (var n in names)
        {
            foreach (var host in new[] { e, type })
            {
                var p = host?.LookupParameter(n);
                if (p is { HasValue: true, StorageType: StorageType.Double } && p.AsDouble() > 0) return p.AsDouble();
            }
        }
        return 0;
    }

    private static bool IsBelowGround(Document doc, Element e, double groundElevationM)
    {
        var levelId = e.LevelId;
        if (levelId == ElementId.InvalidElementId)
        {
            var p = e.get_Parameter(BuiltInParameter.WALL_BASE_CONSTRAINT)
                    ?? e.get_Parameter(BuiltInParameter.FAMILY_LEVEL_PARAM)
                    ?? e.get_Parameter(BuiltInParameter.INSTANCE_REFERENCE_LEVEL_PARAM);
            levelId = p?.AsElementId() ?? ElementId.InvalidElementId;
        }
        if (doc.GetElement(levelId) is not Level lv) return false;
        return lv.Elevation * 0.3048 < groundElevationM - 0.01;
    }
}
