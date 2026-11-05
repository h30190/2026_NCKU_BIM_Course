namespace LebrCarbon.Core.Lebr;

/// <summary>
/// 地上層跨距變化係數 Sp（手冊 4-1 節式 a、b、c）。
/// 輸入為 X、Y 兩向各跨距長度（m），例如由軸線間距取得。
/// </summary>
public static class SpanCalculator
{
    /// <summary>單軸跨距比 a = max(最大/平均, 平均/最小)；單跨或無資料時為 1.0。</summary>
    public static double AxisRatio(IReadOnlyList<double> spans)
    {
        var s = spans.Where(x => x > 0).ToList();
        if (s.Count <= 1) return 1.0;
        var avg = s.Average();
        return Math.Max(s.Max() / avg, avg / s.Min());
    }

    /// <summary>Sp = (ax×Bx + ay×By) / (Bx + By)，且 Sp ≥ 1.0。</summary>
    public static double Sp(IReadOnlyList<double> xSpans, IReadOnlyList<double> ySpans)
    {
        var bx = xSpans.Where(x => x > 0).Sum();
        var by = ySpans.Where(x => x > 0).Sum();
        if (bx + by <= 0) return 1.0;
        var sp = (AxisRatio(xSpans) * bx + AxisRatio(ySpans) * by) / (bx + by);
        return Math.Max(1.0, sp);
    }
}
