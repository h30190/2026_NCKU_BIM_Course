using LebrCarbon.Core.Lebr;
using LebrCarbon.Dashboard;
using LebrCarbon.Data;

namespace LebrCarbon.Core.Tests;

/// <summary>AI 分析計畫書：情境試算與分析引擎。驗收數字取自專案1 在 Revit 與命令列的實測結果。</summary>
public class AnalysisTests
{
    private static readonly BlccLibrary Lib = BlccLibrary.Load();

    private static LebrProjectFile Project1() =>
        ProjectStore.TryLoad(Path.Combine(AppContext.BaseDirectory, "Fixtures", "專案1.lebr.json"))!;

    [Fact]
    public void 沒有替換時與直接重算相同()
    {
        var s = ScenarioRunner.Run(Project1(), Lib, Array.Empty<ScenarioChange>());
        var direct = ProjectEvaluator.Evaluate(Project1(), Lib);
        Assert.Equal(direct.EEC, s.EEC, 6);
        Assert.Equal(direct.CFR, s.CFR, 9);
    }

    [Fact]
    public void 內隔間改磚牆_與Revit實測相同()
    {
        var s = ScenarioRunner.Run(Project1(), Lib, new[] { new ScenarioChange("IW-3", "IW-1") });
        Assert.InRange(s.CFR, 0.16135, 0.16145);   // 16.14%
        Assert.InRange(s.EEC, 122_308.5, 122_309.5);
        Assert.Equal("1級", s.Grade);
    }

    [Fact]
    public void 帷幕牆是否拿掉外裝_減碳量相同但CFR不同()
    {
        var keep = ScenarioRunner.Run(Project1(), Lib, new[] { new ScenarioChange("CW-0", "CW-2") });
        var drop = ScenarioRunner.Run(Project1(), Lib, new[] { new ScenarioChange("CW-0", "CW-2", DropFinish: true) });

        Assert.InRange(keep.CFR, 0.19255, 0.19265);   // 19.26%
        Assert.InRange(drop.CFR, 0.22625, 0.22635);   // 22.63%
        Assert.Equal("1+級", drop.Grade);
        Assert.Equal(keep.DeltaCF, drop.DeltaCF, 3);  // 基準案跟著設計案拿掉 OW-1，減碳量不變
    }

    [Fact]
    public void 指定類型名稱只換那一列()
    {
        var s = ScenarioRunner.Run(Project1(), Lib, new[] { new ScenarioChange("IW-3", "IW-1", TypeName: "LEBR測試 輕隔間 138mm") });
        Assert.InRange(s.CFR, 0.16135, 0.16145);
        Assert.Throws<ScenarioException>(() =>
            ScenarioRunner.Run(Project1(), Lib, new[] { new ScenarioChange("IW-3", "IW-1", TypeName: "不存在的類型") }));
    }

    [Theory]
    [InlineData("IW-3", "XX-9", "不存在")]
    [InlineData("IW-9", "IW-1", "找不到")]
    [InlineData("IW-3", "CW-0", "同類別")]
    public void 不合法的替換會擋下(string from, string to, string message)
    {
        var ex = Assert.Throws<ScenarioException>(() =>
            ScenarioRunner.Run(Project1(), Lib, new[] { new ScenarioChange(from, to) }));
        Assert.Contains(message, ex.Message);
    }

    [Fact]
    public void 試算不改原本的評估輸入()
    {
        var file = Project1();
        ScenarioRunner.Run(file, Lib, new[] { new ScenarioChange("IW-3", "IW-1") });
        Assert.Contains(file.DesignItems, i => i.BlccId == "IW-3");
    }

    [Fact]
    public void 分析的佔比加總與計算結果一致()
    {
        var file = Project1();
        var r = ProjectEvaluator.Evaluate(file, Lib);
        var a = AnalysisBuilder.Build(Project1(), Lib, r);

        Assert.Equal(4, a.Contributions.Count);
        Assert.Equal(r.CFns + r.CFrm, a.Contributions.Sum(c => c.DesignTotal), 3);
        Assert.Equal(1.0, a.Contributions.Sum(c => c.ShareOfNonStructural), 9);
        var s = a.StageShares;
        Assert.Equal(1.0, s.Structure + s.NonStructural + s.Renew + s.Construction + s.Demolition, 9);

        // RC 外牆（CW-0）是非主結構最大宗：32,126 ÷ (47,748 + 8,569)
        var top = a.Contributions.MaxBy(c => c.DesignTotal)!;
        Assert.Equal("CW-0", top.BlccId);
        Assert.InRange(top.ShareOfNonStructural, 0.570, 0.571);
    }

    [Fact]
    public void 改善潛力是單項試算_與情境試算一致()
    {
        var file = Project1();
        var r = ProjectEvaluator.Evaluate(file, Lib);
        var a = AnalysisBuilder.Build(Project1(), Lib, r);

        var iw = a.Potential.Single(p => p.BlccId == "IW-3");
        var brick = iw.Options.Single(o => o.BlccId == "IW-1");
        Assert.InRange(brick.Cfr, 0.16135, 0.16145);
        Assert.True(brick.DeltaCfr < 0);
        Assert.All(iw.Options.Zip(iw.Options.Skip(1)), p => Assert.True(p.First.Cfr >= p.Second.Cfr));
        Assert.True(iw.Best is null || iw.Best.DeltaCfr > 0);

        Assert.True(a.Potential.Single(p => p.BlccId == "CW-0").NeedsRuleCheck);
        Assert.False(iw.NeedsRuleCheck);
    }

    [Theory]
    [InlineData(0.1805, "1+級", 0.20)]
    [InlineData(0.10, "2級", 0.12)]
    [InlineData(-0.5, "6級", -0.20)]
    public void 距下一級(double cfr, string grade, double needed)
    {
        var n = AnalysisBuilder.NextGradeOf(cfr)!;
        Assert.Equal(grade, n.Grade);
        Assert.Equal(needed, n.CfrNeeded, 9);
        Assert.Equal(needed - cfr, n.Gap, 9);
    }

    [Fact]
    public void 已是最高級時沒有下一級() => Assert.Null(AnalysisBuilder.NextGradeOf(0.25));
}
