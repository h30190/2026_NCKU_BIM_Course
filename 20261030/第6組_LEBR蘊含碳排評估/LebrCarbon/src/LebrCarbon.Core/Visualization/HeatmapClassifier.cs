namespace LebrCarbon.Core.Visualization;

/// <summary>一個著色級距：碳排範圍與構件數。</summary>
public sealed record HeatClass(int Index, double Min, double Max, int Count);

/// <summary>
/// 3D 熱點著色的分級（計畫書 F8）。
/// 以分位數分成 5 級，讓每一級的構件數大致相同；
/// 若用等距分級，一塊大樓板就會把其他構件全壓在最低一級，看不出差異。
/// </summary>
public static class HeatmapClassifier
{
    public const int DefaultClasses = 5;

    /// <summary>
    /// 回傳各級的上限（共 classes − 1 個，最高一級無上限）。
    /// 只用大於 0 的值計算；沒有資料時回傳空陣列。
    /// </summary>
    public static double[] Breaks(IEnumerable<double> values, int classes = DefaultClasses)
    {
        if (classes < 2) throw new ArgumentOutOfRangeException(nameof(classes));
        var s = values.Where(v => v > 0 && double.IsFinite(v)).OrderBy(v => v).ToArray();
        if (s.Length == 0) return Array.Empty<double>();

        var breaks = new double[classes - 1];
        for (var k = 1; k < classes; k++)
        {
            var idx = (int)Math.Ceiling((double)k * s.Length / classes) - 1;
            breaks[k - 1] = s[Math.Clamp(idx, 0, s.Length - 1)];
        }
        return breaks;
    }

    /// <summary>值落在哪一級（0 = 最低）。</summary>
    public static int Classify(double value, IReadOnlyList<double> breaks)
    {
        for (var i = 0; i < breaks.Count; i++)
            if (value <= breaks[i]) return i;
        return breaks.Count;
    }

    /// <summary>彙整各級的實際最小、最大值與構件數，供圖例使用。沒有構件的級距不列出。</summary>
    public static List<HeatClass> Summarize(IEnumerable<double> values, IReadOnlyList<double> breaks)
    {
        return values.Where(v => v > 0 && double.IsFinite(v))
            .GroupBy(v => Classify(v, breaks))
            .OrderBy(g => g.Key)
            .Select(g => new HeatClass(g.Key, g.Min(), g.Max(), g.Count()))
            .ToList();
    }
}

/// <summary>單一構件的碳排，分為主結構（材料數量法）與非主結構（B-LCC 新建碳排）兩部分。</summary>
public sealed record ElementCarbon(long ElementId, double Structural, double NonStructural)
{
    public double Total => Structural + NonStructural;
}
