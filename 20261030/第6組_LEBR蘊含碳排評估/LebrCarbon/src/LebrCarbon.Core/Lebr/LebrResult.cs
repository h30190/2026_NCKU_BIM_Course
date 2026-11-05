namespace LebrCarbon.Core.Lebr;

/// <summary>一個情境（設計案或基準案）的主結構計算過程。</summary>
public sealed record StructureScenario(
    double W, double Sp, double F, double LCCR, double RN,
    double C,             // 地上層主結構單位樓地板面積碳排密度（kgCO2e/m2），已套用 C ≥ 165
    double CBeforeFloor,  // 式 4 未套用下限前的值
    double Cu,
    double CFs);

/// <summary>非主結構某一類的新建與更新碳排小計。</summary>
public sealed record NsSubtotal(NsCategory Category, double Area, double New, double Renew);

/// <summary>LEBR 評估結果，保留所有中間值以便追溯（計畫書 2.2 可追溯）。</summary>
public sealed class LebrResult
{
    public required BuildingInputs Inputs { get; init; }

    // 主結構
    public required StructureScenario Design { get; init; }
    public required StructureScenario Baseline { get; init; }
    /// <summary>CFs' 地下層主結構碳排（式 2）。</summary>
    public double CFsUnderground { get; init; }

    // 非主結構
    public required IReadOnlyList<NonStructuralItem> DesignItems { get; init; }
    public required IReadOnlyList<NonStructuralItem> BaselineItems { get; init; }
    public required IReadOnlyList<NsSubtotal> DesignSubtotals { get; init; }
    public required IReadOnlyList<NsSubtotal> BaselineSubtotals { get; init; }
    public double CFns { get; init; }
    public double CFnsBaseline { get; init; }

    // 四階段
    /// <summary>CFum 製造運輸階段（式 9）。</summary>
    public double CFum { get; init; }
    public double CFumBaseline { get; init; }
    /// <summary>CFrm 更新修繕階段（式 10）。</summary>
    public double CFrm { get; init; }
    public double CFrmBaseline { get; init; }
    /// <summary>CFc 地上層施工（式 12），基準案同。</summary>
    public double CFc { get; init; }
    /// <summary>CFc' 地下層施工（式 13）。</summary>
    public double CFcUnderground { get; init; }
    /// <summary>CFdw 地上層拆除廢棄（式 14），基準案同。</summary>
    public double CFdw { get; init; }
    /// <summary>CFdw' 地下層拆除廢棄（式 17）。</summary>
    public double CFdwUnderground { get; init; }
    public double Wd { get; init; }

    // 分級
    /// <summary>EEC 設計案評估範疇蘊含碳排（式 21）。</summary>
    public double EEC { get; init; }
    /// <summary>EECc 基準案評估範疇蘊含碳排（式 20）。</summary>
    public double EECc { get; init; }
    public double TEC { get; init; }
    public double TECc { get; init; }
    public double ECI { get; init; }
    public double ECIs { get; init; }
    public double DeltaCF { get; init; }
    public double CFR { get; init; }
    public required string Grade { get; init; }

    /// <summary>表 12 各等級刻度碳排密度。</summary>
    public IEnumerable<(string Grade, double Eci)> Scale =>
        LebrTables.ScaleTicks.Select(t => (t.Grade, ECIs * t.Multiplier));

    /// <summary>設計案地上層四階段碳排（未扣優惠、未除延壽）。</summary>
    public double DesignAboveGroundTotal => CFum + CFrm + CFc + CFdw;
}
