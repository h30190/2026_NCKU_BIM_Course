using Autodesk.Revit.DB;
using LebrCarbon.Core.Models;

namespace LebrCarbon.Revit.Extraction;

/// <summary>
/// 從模型唯讀擷取主結構材料數量（計畫書第 5 節步驟 1–2）。
/// 輸出只有 Core 的 MaterialQuantity，不把 Revit 物件往外傳。
/// </summary>
public sealed class StructuralQuantityExtractor
{
    public const string ParamFactorId = "LEBR_FactorId";
    public const string ParamScope = "LEBR_Scope";
    public const string ParamRebarRatio = "LEBR_RebarRatio";

    private static readonly BuiltInCategory[] PrimaryCategories =
    {
        BuiltInCategory.OST_StructuralColumns,
        BuiltInCategory.OST_StructuralFraming,
        BuiltInCategory.OST_Floors,
        BuiltInCategory.OST_Walls,
        BuiltInCategory.OST_StructuralFoundation,
    };

    private readonly Document _doc;
    private readonly double _steelDensity;
    private readonly Dictionary<ElementId, Material?> _materialCache = new();

    public StructuralQuantityExtractor(Document doc, double steelDensityKgPerM3)
    {
        _doc = doc;
        _steelDensity = steelDensityKgPerM3 > 0 ? steelDensityKgPerM3 : 7850;
    }

    /// <summary>沒有任何材質體積的元件，交給完整性檢查。</summary>
    public List<UnmappedItem> NoMaterialElements { get; } = new();

    public List<MaterialQuantity> Extract()
    {
        var list = new List<MaterialQuantity>();
        var filter = new ElementMulticategoryFilter(PrimaryCategories);

        var elements = new FilteredElementCollector(_doc)
            .WherePasses(filter)
            .WhereElementIsNotElementType()
            .ToElements();

        foreach (var e in elements)
        {
            if (e.Category is null) continue;
            if (e is Wall wall && !IsStructuralWall(wall)) continue;

            var scope = ReadScope(e);
            if (scope == Scope.Excluded) continue;

            var elementOverride = ReadString(e, ParamFactorId);
            var type = _doc.GetElement(e.GetTypeId());
            var rebarRatio = type?.LookupParameter(ParamRebarRatio) is { HasValue: true } rp ? rp.AsDouble() : (double?)null;

            var matIds = e.GetMaterialIds(false);
            if (matIds.Count == 0)
            {
                NoMaterialElements.Add(new(Id(e), e.Category.Name, "（無材質）", "元件沒有指定材質"));
                continue;
            }

            foreach (var matId in matIds)
            {
                var volFt3 = e.GetMaterialVolume(matId);
                if (volFt3 <= 0) continue;

                var vol = UnitUtils.ConvertFromInternalUnits(volFt3, UnitTypeId.CubicMeters);
                var mat = GetMaterial(matId);
                var matName = mat?.Name ?? "（未知材質）";
                var matClass = mat?.MaterialClass ?? "";
                var isSteel = IsSteel(matName, matClass);

                list.Add(new MaterialQuantity
                {
                    ElementId = Id(e),
                    Category = e.Category.Name,
                    MaterialName = matName,
                    MaterialClass = matClass,
                    Quantity = isSteel ? vol * _steelDensity : vol,
                    Unit = isSteel ? "kg" : "m3",
                    Scope = scope,
                    ElementFactorOverride = elementOverride,
                    MaterialFactorOverride = mat is null ? null : ReadString(mat, ParamFactorId),
                    RebarRatioOverride = rebarRatio,
                });
            }
        }

        return list;
    }

    private static bool IsStructuralWall(Wall wall)
    {
        var p = wall.get_Parameter(BuiltInParameter.WALL_STRUCTURAL_SIGNIFICANT);
        return p is not null && p.AsInteger() == 1;
    }

    private static bool IsSteel(string name, string matClass)
    {
        if (name.Contains("鋼筋") || name.Contains("rebar", StringComparison.OrdinalIgnoreCase)) return false;
        return matClass.Contains("metal", StringComparison.OrdinalIgnoreCase)
               || name.Contains("鋼")
               || name.Contains("steel", StringComparison.OrdinalIgnoreCase);
    }

    private static Scope ReadScope(Element e) => ReadString(e, ParamScope) switch
    {
        "排除" or "Excluded" => Scope.Excluded,
        "非主結構" or "Secondary" => Scope.Secondary,
        _ => Scope.Primary,
    };

    private static string? ReadString(Element e, string name)
    {
        var p = e.LookupParameter(name);
        var s = p?.StorageType == StorageType.String ? p.AsString() : null;
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private Material? GetMaterial(ElementId id)
    {
        if (!_materialCache.TryGetValue(id, out var m))
        {
            m = _doc.GetElement(id) as Material;
            _materialCache[id] = m;
        }
        return m;
    }

    private static string Id(Element e) => e.Id.Value.ToString();
}
