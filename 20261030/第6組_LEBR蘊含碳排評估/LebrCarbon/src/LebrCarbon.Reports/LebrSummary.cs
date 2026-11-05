using System.Globalization;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Reports;

/// <summary>摘要報表的抬頭資訊：哪個模型、何時評估、用哪一版外掛與手冊。</summary>
public sealed record SummaryMeta(
    string ModelTitle,
    DateTimeOffset EvaluatedAt,
    string PluginVersion,
    string Manual = "LEBR-2025",
    ShapeValues? Shape = null);

public sealed record SummaryStat(string Label, string Value, string Unit);

public sealed record SummaryStageRow(string Label, double Design, double Baseline)
{
    public double Diff => Design - Baseline;
}

public sealed record SummaryItemRow(string BlccId, string Category, string Name, double Area, double NewCarbon, double RenewCarbon)
{
    public double Total => NewCarbon + RenewCarbon;
}

/// <summary>
/// 一頁式 PDF 摘要的內容（計畫書 F10）。只整理數字與文字，不計算碳排：所有數字都直接取自 LebrResult，
/// 格式與 Excel 計算書一致（碳排千分位、CFR 兩位小數）。繪製交給 LebrCarbon.Reports.Pdf。
/// </summary>
public sealed record LebrSummary
{
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public required string Grade { get; init; }
    /// <summary>是否達公有建築 2 級門檻（1+、1、2 級），供徽章配色；文字仍以等級本身表達。</summary>
    public bool MeetsPublicThreshold { get; init; }
    public required string CfrText { get; init; }
    public required IReadOnlyList<SummaryStat> Stats { get; init; }
    public double Eci { get; init; }
    public double Ecis { get; init; }
    /// <summary>表 12 的 7 個刻度（等級, ECI），分出 8 個等級。</summary>
    public required IReadOnlyList<(string Grade, double Eci)> ScaleTicks { get; init; }
    /// <summary>地上層五段：主結構、非主結構新建、更新修繕、施工、拆除廢棄。</summary>
    public required IReadOnlyList<SummaryStageRow> Stages { get; init; }
    public required IReadOnlyList<(string Label, string Value)> Parameters { get; init; }
    /// <summary>設計案非主結構構件，依合計碳排由大到小。</summary>
    public required IReadOnlyList<SummaryItemRow> Items { get; init; }
    public required IReadOnlyList<string> Notes { get; init; }
    public string Disclaimer { get; init; } = "設計自主檢討用，非正式標示結果。正式標示須經評定專業機構審查。";
    public required string Footer { get; init; }

    public double DesignTotal => Stages.Sum(s => s.Design);
    public double BaselineTotal => Stages.Sum(s => s.Baseline);
}

public static class LebrSummaryBuilder
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public static string N0(double v) => v.ToString("N0", Inv);
    public static string N2(double v) => v.ToString("N2", Inv);

    private static readonly HashSet<string> Passing = new() { "1+級", "1級", "2級" };

    /// <summary>公有建築門檻（2027 起至少 2 級）：1+、1、2 級為達標。</summary>
    public static bool MeetsPublicThreshold(string grade) => Passing.Contains(grade);

    public static LebrSummary Build(LebrResult r, SummaryMeta meta)
    {
        var p = r.Inputs;
        var credits = p.ReuseCredit + p.RecycledMaterialCredit + p.LowCarbonMethodCredit;

        var notes = new List<string>();
        if (r.Design.CBeforeFloor < LebrCalculator.MinC)
            notes.Add($"設計案主結構 C 計算值 {r.Design.CBeforeFloor:0.00} 低於下限，依手冊式 4 以 {LebrCalculator.MinC:0} 計。");
        if (credits > 0 || p.LL > 0)
            notes.Add($"EEC 已扣減碳優惠 {N0(credits)} kgCO2e，並除以（1 + 延壽係數 LL {p.LL:0.00}）。");
        notes.Add("施工與拆除廢棄依手冊式 12～19 只以設計案計算一次，基準案同。");
        notes.Add($"地下層（不列入分級）：主結構 {N0(r.CFsUnderground)}、施工 {N0(r.CFcUnderground)}、拆除廢棄 {N0(r.CFdwUnderground)} kgCO2e。");

        var shape = meta.Shape;
        return new LebrSummary
        {
            Title = "LEBR 低碳建築評估摘要",
            Subtitle = $"{meta.ModelTitle}　｜　評估時間 {meta.EvaluatedAt:yyyy-MM-dd HH:mm}　｜　手冊 {meta.Manual}　｜　LebrCarbon {meta.PluginVersion}",
            Grade = r.Grade,
            MeetsPublicThreshold = MeetsPublicThreshold(r.Grade),
            // 與網頁、Excel 一致寫成「18.05%」（.NET 的 P2 格式會在 % 前多一個空格）
            CfrText = (r.CFR * 100).ToString("0.00", Inv) + "%",
            Stats = new[]
            {
                // 兩兩對照排列：設計案｜基準案
                new SummaryStat("設計案 EEC", N0(r.EEC), "kgCO2e"),
                new SummaryStat("基準案 EECc", N0(r.EECc), "kgCO2e"),
                new SummaryStat("碳排密度 ECI", N2(r.ECI), "kgCO2e/m²"),
                new SummaryStat("基準 ECIs", N2(r.ECIs), "kgCO2e/m²"),
                new SummaryStat("減碳量 ΔCF", N0(r.DeltaCF), "kgCO2e"),
                new SummaryStat("全建築 TEC（含地下層）", N0(r.TEC), "kgCO2e"),
            },
            Eci = r.ECI,
            Ecis = r.ECIs,
            ScaleTicks = r.Scale.ToList(),
            Stages = new[]
            {
                new SummaryStageRow("主結構", r.Design.CFs, r.Baseline.CFs),
                new SummaryStageRow("非主結構新建", r.CFns, r.CFnsBaseline),
                new SummaryStageRow("更新修繕", r.CFrm, r.CFrmBaseline),
                new SummaryStageRow("施工", r.CFc, r.CFc),
                new SummaryStageRow("拆除廢棄", r.CFdw, r.CFdw),
            },
            Parameters = new (string, string)[]
            {
                ("地上／地下樓地板面積", $"{N2(p.AFu)}／{N2(p.AFb)} m²"),
                ("地上／地下樓層數", $"{p.S:0.##}／{p.Sb:0.##}"),
                ("一樓樓高 BH", $"{p.BH:0.##} m"),
                ("構造類別", p.Structure.DisplayName()),
                ("構造係數 W（基準）", $"{r.Design.W:0.00}（{r.Baseline.W:0.00}）"),
                ("跨距係數 Sp（基準）", $"{r.Design.Sp:0.000}（{r.Baseline.Sp:0.000}）"),
                ("形狀係數 F（基準）", $"{r.Design.F:0.000}（{r.Baseline.F:0.000}）"),
                ("PAr／長寬比 b／出挑 Rc", shape is null ? "—" : $"{shape.PAr:0.###}／{shape.Aspect:0.###}／{shape.Rc:0.###}"),
                ("I／SaD／Fu／Rs", $"{p.I}／{p.SaD}／{p.Fu}／{p.Rs}"),
                ("活載重 L／額外靜載重 D0", $"{p.L}／{p.D0} kgf/m²"),
                ("耗損等級／廢棄物用途", $"{WearName(p.Wear)}／{WasteName(p.WasteUsage)}"),
            },
            Items = r.DesignItems
                .Select(i => new SummaryItemRow(i.BlccId ?? "（自訂）", i.Category.DisplayName(), i.Name, i.Area, i.NewEmission, i.RenewEmission))
                .OrderByDescending(i => i.Total)
                .ToList(),
            Notes = notes,
            Footer = $"LebrCarbon {meta.PluginVersion}・{meta.Manual}・產生於 {meta.EvaluatedAt:yyyy-MM-dd HH:mm}",
        };
    }

    private static string WearName(WearLevel w) => w switch
    {
        WearLevel.High => "高耗損",
        WearLevel.Mid => "中耗損",
        _ => "低耗損",
    };

    private static string WasteName(WasteUsage u) => u switch
    {
        WasteUsage.Residential => "住宿類與旅館、醫院",
        WasteUsage.Factory => "廠房類",
        _ => "其他非住宿類",
    };
}
