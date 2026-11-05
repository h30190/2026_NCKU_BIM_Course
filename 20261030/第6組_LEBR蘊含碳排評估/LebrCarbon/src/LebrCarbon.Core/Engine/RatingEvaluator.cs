namespace LebrCarbon.Core.Engine;

/// <summary>
/// 減碳率 R = 1 − ECI / ECI_baseline，再對照等級表（計畫書第 5 節步驟 6）。
/// 基準值與等級門檻必須依 LEBR 2025 手冊填入，這裡不寫死任何數字。
/// </summary>
public sealed class RatingEvaluator
{
    /// <summary>等級門檻：減碳率 ≥ MinReduction 即達該等級。由資料層載入。</summary>
    public sealed record Grade(string Name, double MinReduction);

    private readonly List<Grade> _grades;

    public RatingEvaluator(IEnumerable<Grade> grades)
    {
        _grades = grades.OrderByDescending(g => g.MinReduction).ToList();
    }

    public static double ReductionRate(double eci, double eciBaseline)
    {
        if (eciBaseline <= 0) throw new ArgumentOutOfRangeException(nameof(eciBaseline), "基準值必須大於 0");
        return 1 - eci / eciBaseline;
    }

    /// <summary>回傳達到的最高等級，未達任何等級時回傳 null。</summary>
    public Grade? Evaluate(double reductionRate) =>
        _grades.FirstOrDefault(g => reductionRate >= g.MinReduction);
}
