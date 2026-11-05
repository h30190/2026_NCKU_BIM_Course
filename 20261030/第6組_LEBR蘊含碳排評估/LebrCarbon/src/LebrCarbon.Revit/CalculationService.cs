using Autodesk.Revit.DB;
using LebrCarbon.Core.Engine;
using LebrCarbon.Core.Mapping;
using LebrCarbon.Core.Models;
using LebrCarbon.Data;
using LebrCarbon.Revit.Extraction;

namespace LebrCarbon.Revit;

/// <summary>串起「擷取 → 補鋼筋 → 對應 → 計算」的流程，供各按鈕共用。</summary>
public static class CalculationService
{
    public static CalcResult Run(Document doc)
    {
        var lib = DataLoader.LoadFactors();
        var rules = DataLoader.LoadRules();

        var extractor = new StructuralQuantityExtractor(doc, lib.GetDefault("steelDensity"));
        var quantities = extractor.Extract();

        var withRebar = RebarEstimator.AddEstimatedRebar(quantities, lib.GetDefault("rebarRatio"));

        var mapper = new MaterialMapper(lib.Factors, rules.Rules);
        var result = new StageCalculator(mapper, lib.Manual).Calculate(withRebar);
        result.Unmapped.AddRange(extractor.NoMaterialElements);

        // 第一版暫存最後一次結果，之後改成快照（計畫書 4.4 節）
        LastResult = result;
        LastQuantities = quantities;
        return result;
    }

    public static CalcResult? LastResult { get; private set; }
    public static IReadOnlyList<MaterialQuantity>? LastQuantities { get; private set; }
}
