using LebrCarbon.Core.Models;

namespace LebrCarbon.Core.Engine;

/// <summary>
/// 鋼筋未建模時，以混凝土體積 × 配筋量（kg/m3）估算鋼筋重量（計畫書第 5 節步驟 4）。
/// 配筋量優先採元件類型上的 LEBR_RebarRatio，否則用手冊預設值。
/// </summary>
public static class RebarEstimator
{
    public const string RebarMaterialName = "鋼筋（依配筋量估算）";
    public const string RebarFactorId = "REBAR";

    public static IEnumerable<MaterialQuantity> AddEstimatedRebar(
        IEnumerable<MaterialQuantity> quantities, double defaultRatioKgPerM3)
    {
        foreach (var q in quantities)
        {
            yield return q;

            if (q.Scope != Scope.Primary || !q.IsConcrete || q.Unit != "m3") continue;

            var ratio = q.RebarRatioOverride ?? defaultRatioKgPerM3;
            if (ratio <= 0) continue;

            yield return q with
            {
                MaterialName = RebarMaterialName,
                MaterialClass = "rebar",
                Quantity = q.Quantity * ratio,
                Unit = "kg",
                ElementFactorOverride = null,
                MaterialFactorOverride = RebarFactorId,
                RebarRatioOverride = null,
            };
        }
    }
}
