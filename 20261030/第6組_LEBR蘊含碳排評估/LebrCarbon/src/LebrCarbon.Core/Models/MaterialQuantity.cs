namespace LebrCarbon.Core.Models;

/// <summary>
/// 從模型（或其他來源）擷取的一筆材料數量。
/// 由介接層產生，計算核心只看這個物件，不碰 Revit API。
/// </summary>
public sealed record MaterialQuantity
{
    /// <summary>來源元件 ID（Revit ElementId 的數值，或其他系統的識別碼）。</summary>
    public required string ElementId { get; init; }
    public required string Category { get; init; }       // 結構柱、樓板 ...
    public required string MaterialName { get; init; }
    /// <summary>材質類別（Revit Material.MaterialClass，例如 Concrete、Metal），用來判斷是否需估算鋼筋。</summary>
    public string MaterialClass { get; init; } = "";
    public double Quantity { get; init; }
    public required string Unit { get; init; }
    public Scope Scope { get; init; } = Scope.Primary;
    /// <summary>元件或材質上的 LEBR_FactorId（人工覆寫），沒有則為 null。</summary>
    public string? ElementFactorOverride { get; init; }
    public string? MaterialFactorOverride { get; init; }
    /// <summary>元件類型上的 LEBR_RebarRatio（kg/m3），沒有則為 null。</summary>
    public double? RebarRatioOverride { get; init; }

    /// <summary>是否為混凝土（材質類別或名稱判斷）。</summary>
    public bool IsConcrete =>
        MaterialClass.Contains("concrete", StringComparison.OrdinalIgnoreCase)
        || MaterialClass.Contains("混凝土")
        || MaterialName.Contains("混凝土")
        || MaterialName.Contains("concrete", StringComparison.OrdinalIgnoreCase);
}

public enum Scope { Primary, Secondary, Excluded }
