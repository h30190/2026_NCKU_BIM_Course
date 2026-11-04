namespace LebrCarbon.Core.Lebr;

/// <summary>手冊中的查表規則。所有數字皆出自 LEBR 2025 年版，表號標於各方法。</summary>
public static class LebrTables
{
    /// <summary>表 3 構造係數 W。</summary>
    public static double StructureFactorW(StructureType t) => t switch
    {
        StructureType.Masonry => 1.2,
        StructureType.RC => 1.00,
        StructureType.SRC => 1.05,
        StructureType.Steel => 0.9,
        StructureType.LightSteel => 0.8,
        StructureType.Wood => 0.7,
        _ => 1.0,
    };

    public static string DisplayName(this StructureType t) => t switch
    {
        StructureType.Masonry => "磚石構造",
        StructureType.RC => "RC 構造",
        StructureType.SRC => "SRC 構造",
        StructureType.Steel => "S 構造",
        StructureType.LightSteel => "輕鋼構造",
        StructureType.Wood => "木構造",
        _ => t.ToString(),
    };

    /// <summary>式 9：周長面積比係數 PAr = 0.282 × P / √A。</summary>
    public static double PerimeterAreaRatio(double perimeter, double area) =>
        area > 0 ? 0.282 * perimeter / Math.Sqrt(area) : 0;

    /// <summary>表 5 平面形狀不規則修正係數 f1。</summary>
    public static double F1(double par) => par <= 1.2 ? 1.00 : par <= 1.4 ? 1.03 : par <= 1.6 ? 1.05 : 1.08;

    /// <summary>表 6 平面長寬比修正係數 f2。</summary>
    public static double F2(double aspect) => aspect <= 4 ? 1.00 : aspect <= 6 ? 1.05 : 1.10;

    /// <summary>表 7 平面出挑修正係數 f3（出挑深度 ≤ 4m 才適用）。</summary>
    public static double F3(double cantileverRatio) =>
        cantileverRatio <= 0.1 ? 1.00 : cantileverRatio <= 0.2 ? 0.98 : 0.93;

    /// <summary>表 8 基準案構造係數：25F 以下 1.0，26F 以上 0.9。</summary>
    public static double BaselineW(double stories) => stories <= 25 ? 1.0 : 0.9;

    /// <summary>表 8 基準案跨距變化係數 Sp，依地上單層平均樓地板面積 AFa 與樓層數。</summary>
    public static double BaselineSp(double stories, double averageFloorArea)
    {
        if (averageFloorArea > 500)
            return stories <= 8 ? 2.0 : stories <= 16 ? 1.8 : stories <= 25 ? 1.6 : 1.4;
        if (averageFloorArea >= 200)
            return stories <= 8 ? 1.8 : stories <= 16 ? 1.6 : 1.4;
        return stories <= 8 ? 1.6 : 1.4;
    }

    /// <summary>表 8 基準案形狀係數 F。</summary>
    public static double BaselineF(double stories) =>
        stories <= 8 ? 1.2 : stories <= 16 ? 1.15 : stories <= 25 ? 1.1 : 1.05;

    /// <summary>表 9 新建與拆除廢棄物量密度 Wd（kg/m2）。木構造以輕鋼構造代用（註 1）。</summary>
    public static double WasteDensity(StructureType t, WasteUsage u) => t switch
    {
        StructureType.SRC => u switch { WasteUsage.Residential => 405, WasteUsage.Factory => 315, _ => 321 },
        StructureType.Steel => u switch { WasteUsage.Residential => 309, WasteUsage.Factory => 318, _ => 270 },
        StructureType.LightSteel or StructureType.Wood => u == WasteUsage.Factory ? 306 : 270,
        // RC 與表外的磚石構造
        _ => u switch { WasteUsage.Residential => 390, WasteUsage.Factory => 240, _ => 294 },
    };

    /// <summary>表 10 LEBR 分級評估間距：CFR 大於門檻即達該等級，由好到差排列；都不到為 7 級。</summary>
    public static IReadOnlyList<(string Grade, double MinCfrExclusive)> GradeThresholds { get; } = new[]
    {
        ("1+級", 0.20), ("1級", 0.16), ("2級", 0.12), ("3級", 0.08),
        ("4級", 0.03), ("5級", -0.10), ("6級", -0.20),
    };

    public const string LowestGrade = "7級";

    /// <summary>表 10 LEBR 分級評估間距。</summary>
    public static string Grade(double cfr)
    {
        foreach (var (grade, min) in GradeThresholds)
            if (cfr > min) return grade;
        return LowestGrade;
    }

    /// <summary>表 12 八等級尺標刻度：ECIs 乘以各等級係數。</summary>
    public static IReadOnlyList<(string Grade, double Multiplier)> ScaleTicks { get; } = new[]
    {
        ("1+級", 0.80), ("1級", 0.84), ("2級", 0.88), ("3級", 0.92),
        ("4級", 0.97), ("5級", 1.10), ("6級", 1.20),
    };

    /// <summary>表 11 建築延壽優惠係數上限。</summary>
    public const double MaxLifeExtension = 0.08;
}
