using LebrCarbon.Core.Mapping;
using LebrCarbon.Core.Models;

namespace LebrCarbon.Core.Engine;

/// <summary>
/// 核心公式 EC_total = Σ_s Σ_i Q_i × EF_i,s（計畫書第 5 節）。
/// 第一版只處理 A1-A3（製造）係數；其他階段之後以額外係數或人工輸入加入。
/// </summary>
public sealed class StageCalculator
{
    private readonly MaterialMapper _mapper;
    private readonly string _manualVersion;

    public StageCalculator(MaterialMapper mapper, string manualVersion)
    {
        _mapper = mapper;
        _manualVersion = manualVersion;
    }

    public CalcResult Calculate(IEnumerable<MaterialQuantity> quantities, double floorArea = 0)
    {
        var result = new CalcResult { ManualVersion = _manualVersion, FloorArea = floorArea };

        foreach (var q in quantities)
        {
            if (q.Scope == Scope.Excluded) continue;

            if (q.Quantity <= 0)
            {
                result.Unmapped.Add(new(q.ElementId, q.Category, q.MaterialName, "數量為 0 或無法取得"));
                continue;
            }

            var map = _mapper.Map(q);
            if (!map.Success)
            {
                result.Unmapped.Add(new(q.ElementId, q.Category, q.MaterialName, map.Error!));
                continue;
            }

            var f = map.Factor!;
            if (!string.Equals(f.Unit, q.Unit, StringComparison.OrdinalIgnoreCase))
            {
                result.Unmapped.Add(new(q.ElementId, q.Category, q.MaterialName,
                    $"單位不符：數量為 {q.Unit}，係數 {f.Id} 為 {f.Unit}"));
                continue;
            }

            result.Lines.Add(new CarbonLine(
                q.ElementId, q.Category, q.MaterialName, q.Quantity, q.Unit,
                f.Id, f.Ef, f.Stage, f.Source, q.Quantity * f.Ef));
        }

        return result;
    }
}
