namespace LebrCarbon.Core.Models;

/// <summary>單一元件、單一材料、單一階段的碳排明細，可追溯到元件與係數（2.2 可追溯）。</summary>
public sealed record CarbonLine(
    string ElementId,
    string Category,
    string MaterialName,
    double Quantity,
    string Unit,
    string FactorId,
    double Ef,
    string Stage,
    string FactorSource,
    double Emission);

/// <summary>對應不到係數的項目，送入完整性檢查清單。</summary>
public sealed record UnmappedItem(string ElementId, string Category, string MaterialName, string Reason);

public sealed class CalcResult
{
    public required string ManualVersion { get; init; }
    public DateTime CalculatedAt { get; init; } = DateTime.Now;
    public List<CarbonLine> Lines { get; } = new();
    public List<UnmappedItem> Unmapped { get; } = new();

    public double Total => Lines.Sum(l => l.Emission);

    public IReadOnlyDictionary<string, double> ByStage =>
        Lines.GroupBy(l => l.Stage).ToDictionary(g => g.Key, g => g.Sum(l => l.Emission));

    public IReadOnlyDictionary<string, double> ByCategory =>
        Lines.GroupBy(l => l.Category).ToDictionary(g => g.Key, g => g.Sum(l => l.Emission));

    /// <summary>計算樓地板面積 m2（手冊定義），未設定時為 0。</summary>
    public double FloorArea { get; set; }

    /// <summary>蘊含碳排密度 ECI = EC_total / A_floor（kgCO2e/m2）。</summary>
    public double? Eci => FloorArea > 0 ? Total / FloorArea : null;
}
