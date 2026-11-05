using LebrCarbon.Core.Lebr;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;
using LebrCarbon.Reports;

namespace LebrCarbon.Core.Tests;

/// <summary>PDF 摘要（F10）的內容：數字直接取自 LebrResult，格式與 Excel、網頁一致。</summary>
public class SummaryTests
{
    private static readonly SummaryMeta Meta = new("測試", new DateTimeOffset(2026, 10, 29, 9, 30, 0, TimeSpan.FromHours(8)), "0.5.0",
        Shape: new ShapeValues(1.172, 1.75, 0));

    private static LebrResult Example() =>
        LebrCalculator.Evaluate(LebrExampleTests.Inputs(), LebrExampleTests.Design(), LebrExampleTests.Baseline());

    [Fact]
    public void 數字與等級取自計算結果()
    {
        var r = Example();
        var s = LebrSummaryBuilder.Build(r, Meta);

        Assert.Equal("1+級", s.Grade);
        Assert.True(s.MeetsPublicThreshold);
        Assert.Equal($"{r.CFR * 100:0.00}%", s.CfrText);
        Assert.DoesNotContain(" %", s.CfrText);
        Assert.Equal(r.ECI, s.Eci);
        Assert.Equal(LebrTables.ScaleTicks.Count, s.ScaleTicks.Count);
        Assert.Contains(s.Stats, x => x.Label == "設計案 EEC" && x.Value == r.EEC.ToString("N0", System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains("0.5.0", s.Footer);
        Assert.Contains("2026-10-29 09:30", s.Subtitle);
    }

    [Fact]
    public void 五段合計與EEC_EECc一致()
    {
        var r = Example();
        var s = LebrSummaryBuilder.Build(r, Meta);
        var p = r.Inputs;
        var credits = p.ReuseCredit + p.RecycledMaterialCredit + p.LowCarbonMethodCredit;

        Assert.Equal(5, s.Stages.Count);
        Assert.Equal(r.EEC, (s.DesignTotal - credits) / (1 + p.LL), 3);
        Assert.Equal(r.EECc, s.BaselineTotal, 3);
        // 附錄三實例有延壽優惠 LL = 0.05，附註要說明
        Assert.Contains(s.Notes, n => n.Contains("LL"));
    }

    [Fact]
    public void 構件依合計碳排排序()
    {
        var r = Example();
        var s = LebrSummaryBuilder.Build(r, Meta);

        Assert.Equal(r.DesignItems.Count, s.Items.Count);
        Assert.Equal(s.Items.OrderByDescending(i => i.Total).Select(i => i.Total), s.Items.Select(i => i.Total));
        Assert.Equal(r.CFns + r.CFrm, s.Items.Sum(i => i.Total), 3);
    }

    [Fact]
    public void 專案1_C值低於下限時附註說明()
    {
        var file = ProjectStore.TryLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "專案1.lebr.json"))!;
        var r = ProjectEvaluator.Evaluate(file, BlccLibrary.Load());
        var s = LebrSummaryBuilder.Build(r, Meta);

        Assert.Equal("18.05%", s.CfrText);
        Assert.Contains(s.Notes, n => n.Contains("低於下限"));
        Assert.DoesNotContain(s.Notes, n => n.Contains("延壽"));   // 專案1 沒有優惠與延壽
    }

    [Theory]
    [InlineData("1+級", true)]
    [InlineData("2級", true)]
    [InlineData("3級", false)]
    [InlineData("7級", false)]
    public void 公有建築門檻判定(string grade, bool pass) =>
        Assert.Equal(pass, LebrSummaryBuilder.MeetsPublicThreshold(grade));
}
