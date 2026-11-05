using System.Text.RegularExpressions;
using LebrCarbon.Core.Models;

namespace LebrCarbon.Core.Mapping;

/// <summary>
/// 依計畫書 4.2 節的順序把材料對到係數：
/// 1. 元件 LEBR_FactorId → 2. 材質 LEBR_FactorId → 3. 規則檔正規表示式 → 4. 未對應。
/// </summary>
public sealed class MaterialMapper
{
    private readonly IReadOnlyDictionary<string, CarbonFactor> _factors;
    private readonly List<(Regex Regex, MappingRule Rule)> _rules;

    public MaterialMapper(IEnumerable<CarbonFactor> factors, IEnumerable<MappingRule> rules)
    {
        _factors = factors.ToDictionary(f => f.Id, StringComparer.OrdinalIgnoreCase);
        _rules = rules
            .Select(r => (new Regex(r.Pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), r))
            .ToList();
    }

    public MapOutcome Map(MaterialQuantity q)
    {
        if (!string.IsNullOrWhiteSpace(q.ElementFactorOverride))
            return Lookup(q.ElementFactorOverride!, "元件 LEBR_FactorId");

        if (!string.IsNullOrWhiteSpace(q.MaterialFactorOverride))
            return Lookup(q.MaterialFactorOverride!, "材質 LEBR_FactorId");

        foreach (var (regex, rule) in _rules)
            if (regex.IsMatch(q.MaterialName))
                return Lookup(rule.FactorId, $"規則 {rule.Pattern}");

        return MapOutcome.Fail("材質名稱沒有符合的對應規則");
    }

    private MapOutcome Lookup(string id, string via) =>
        _factors.TryGetValue(id, out var f)
            ? MapOutcome.Ok(f, via)
            : MapOutcome.Fail($"{via} 指定的係數 {id} 不在係數庫中");
}

public sealed record MapOutcome(CarbonFactor? Factor, string Via, string? Error)
{
    public bool Success => Factor is not null;
    public static MapOutcome Ok(CarbonFactor f, string via) => new(f, via, null);
    public static MapOutcome Fail(string error) => new(null, "", error);
}
