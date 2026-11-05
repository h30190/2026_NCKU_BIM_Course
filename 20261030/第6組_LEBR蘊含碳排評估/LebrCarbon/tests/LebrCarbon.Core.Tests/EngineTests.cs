using LebrCarbon.Core.Engine;
using LebrCarbon.Core.Mapping;
using LebrCarbon.Core.Models;
using LebrCarbon.Data;

namespace LebrCarbon.Core.Tests;

/// <summary>
/// 這裡的係數是測試用的假數字，只驗證公式與流程。
/// 拿到手冊範例後，新增一組「以手冊範例為輸入、結果須一致」的測試（計畫書第 8 節）。
/// </summary>
public class EngineTests
{
    private static readonly CarbonFactor[] Factors =
    {
        new() { Id = "CONC-280", Name = "混凝土280", Category = "concrete", Unit = "m3", Ef = 300 },
        new() { Id = "REBAR", Name = "鋼筋", Category = "rebar", Unit = "kg", Ef = 2 },
        new() { Id = "STEEL-H", Name = "型鋼", Category = "steel", Unit = "kg", Ef = 3 },
    };

    private static readonly MappingRule[] Rules =
    {
        new() { Pattern = "混凝土.*280", FactorId = "CONC-280" },
        new() { Pattern = "鋼", FactorId = "STEEL-H" },
    };

    private static MaterialQuantity Q(string id, string mat, double qty, string unit = "m3",
        string? elemOverride = null, string? matOverride = null) => new()
    {
        ElementId = id, Category = "結構柱", MaterialName = mat, Quantity = qty, Unit = unit,
        ElementFactorOverride = elemOverride, MaterialFactorOverride = matOverride,
    };

    private static StageCalculator Calc() => new(new MaterialMapper(Factors, Rules), "TEST");

    [Fact]
    public void 數量乘係數後加總()
    {
        var r = Calc().Calculate(new[] { Q("1", "混凝土 280", 10), Q("2", "混凝土-280", 5) });
        Assert.Equal(4500, r.Total, 6);
        Assert.Empty(r.Unmapped);
    }

    [Fact]
    public void 元件覆寫優先於材質覆寫與規則()
    {
        var m = new MaterialMapper(Factors, Rules);
        var o = m.Map(Q("1", "混凝土 280", 1, "kg", elemOverride: "REBAR", matOverride: "STEEL-H"));
        Assert.Equal("REBAR", o.Factor!.Id);
        Assert.Contains("元件", o.Via);
    }

    [Fact]
    public void 找不到規則的材質列入未對應()
    {
        var r = Calc().Calculate(new[] { Q("9", "木材", 3) });
        Assert.Equal(0, r.Total);
        Assert.Single(r.Unmapped);
    }

    [Fact]
    public void 單位不符不計入並列出()
    {
        var r = Calc().Calculate(new[] { Q("1", "混凝土 280", 3, "kg") });
        Assert.Empty(r.Lines);
        Assert.Contains("單位不符", r.Unmapped[0].Reason);
    }

    [Fact]
    public void 排除的元件不計算()
    {
        var q = Q("1", "混凝土 280", 10) with { Scope = Scope.Excluded };
        var r = Calc().Calculate(new[] { q });
        Assert.Empty(r.Lines);
        Assert.Empty(r.Unmapped);
    }

    [Fact]
    public void 混凝土依配筋量估算鋼筋()
    {
        var input = RebarEstimator.AddEstimatedRebar(new[] { Q("1", "混凝土 280", 10) }, 100).ToList();
        Assert.Equal(2, input.Count);
        Assert.Equal(1000, input[1].Quantity);

        var r = Calc().Calculate(input);
        Assert.Equal(10 * 300 + 1000 * 2, r.Total, 6);
    }

    [Fact]
    public void 類型配筋量覆寫預設值()
    {
        var q = Q("1", "混凝土 280", 10) with { RebarRatioOverride = 150 };
        var input = RebarEstimator.AddEstimatedRebar(new[] { q }, 100).ToList();
        Assert.Equal(1500, input[1].Quantity);
    }

    [Fact]
    public void 碳排密度與減碳率()
    {
        var r = Calc().Calculate(new[] { Q("1", "混凝土 280", 10) }, floorArea: 100);
        Assert.Equal(30, r.Eci!.Value, 6);
        Assert.Equal(0.25, RatingEvaluator.ReductionRate(30, 40), 6);
    }

    [Fact]
    public void 等級取達到的最高級()
    {
        var ev = new RatingEvaluator(new RatingEvaluator.Grade[] { new("1級", 0.4), new("2級", 0.2), new("3級", 0.1) });
        Assert.Equal("2級", ev.Evaluate(0.25)!.Name);
        Assert.Null(ev.Evaluate(0.05));
    }

    [Fact]
    public void 隨附的資料檔可以讀取且係數ID都存在()
    {
        var lib = DataLoader.LoadFactors();
        var rules = DataLoader.LoadRules();
        Assert.Equal("LEBR-2025", lib.Manual);
        var ids = lib.Factors.Select(f => f.Id).ToHashSet();
        Assert.All(rules.Rules, r => Assert.Contains(r.FactorId, ids));
        Assert.Contains("REBAR", ids);
    }
}
