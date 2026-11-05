using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Core.Tests;

/// <summary>
/// 以手冊附錄三「高雄市 Z 社會住宅」實例驗證整套 LEBR 算法。
/// 手冊在中間步驟有四捨五入（Sp 取 1.46、LCCR 取 0.915），因此容許 0.1% 誤差。
/// 已知手冊筆誤：戶外地坪 CFp3「RC 底+磁磚」更新碳排列為 108.38，但小計實際採用 48.28（附表 2-7 低耗損值），此處採 48.28。
/// </summary>
public class LebrExampleTests
{
    internal static BuildingInputs Inputs() => new()
    {
        AFu = 56_259.80, AFb = 23_120.49, S = 14, Sb = 3, BH = 4.2,
        I = 1.0, SaD = 0.298, Fu = 3.0, Rs = 1.0, L = 205, D0 = 300,
        Structure = StructureType.RC, Sp = 1.46, F = 1.08, CSER = 1.705, EBF = 0,
        Wear = WearLevel.Low, WasteUsage = WasteUsage.Residential, LL = 0.05,
    };

    private static NonStructuralItem N(NsCategory c, string name, double area, double ef, double renew) =>
        new() { Category = c, Name = name, Area = area, NewEf = ef, RenewEf = renew };

    internal static List<NonStructuralItem> Design() => new()
    {
        N(NsCategory.ExteriorFinish, "金屬模板搗灌 RC 外牆貼磁磚", 39_381.7, 19.65, 19.65),
        N(NsCategory.Window, "鋁框+5+5mm", 2_685.07, 48.84, 0),
        N(NsCategory.Window, "鋁框+6+6mm", 1_892.10, 53.64, 0),
        N(NsCategory.Window, "鋁框+清10mm", 494.07, 45.34, 0),
        N(NsCategory.Window, "鋁框+清5mm", 682.56, 35.09, 0),
        N(NsCategory.CurtainWall, "傳統 15cm RC 外牆", 39_381.70, 73.67, 0),
        N(NsCategory.InteriorWall, "輕質灌漿牆", 33_266.42, 31.83, 0),
        N(NsCategory.InteriorWall, "RC 隔間牆", 32.4, 73.67, 0),
        N(NsCategory.IndoorFloor, "Epoxy 地坪", 253.12, 21.10, 3.76),
        N(NsCategory.IndoorFloor, "貼磁磚地坪", 21_675.52, 33.23, 9.825),
        N(NsCategory.IndoorFloor, "橡膠緩衝墊水泥砂漿+貼地磚", 30_001.85, 35.66, 9.825),
        N(NsCategory.OutdoorFloor, "RC 底+抿石子", 233.89, 96.44, 36.34),
        N(NsCategory.OutdoorFloor, "RC 底+高壓磚", 1_066.32, 144.65, 84.55),
        N(NsCategory.OutdoorFloor, "RC 底+磁磚", 1_964.17, 108.38, 48.28),
        N(NsCategory.OutdoorFloor, "RC 底+水泥粉刷", 51.66, 78.2, 18.1),
    };

    internal static List<NonStructuralItem> Baseline() => new()
    {
        N(NsCategory.ExteriorFinish, "貼磁磚", 39_381.7, 28.7, 19.65),
        N(NsCategory.Window, "鋁框+5+5mm", 2_685.07, 48.84, 0),
        N(NsCategory.Window, "鋁框+6+6mm", 1_892.10, 53.64, 0),
        N(NsCategory.Window, "鋁框+清10mm", 494.07, 45.34, 0),
        N(NsCategory.Window, "鋁框+清5mm", 682.56, 35.09, 0),
        N(NsCategory.CurtainWall, "傳統 15cm RC 外牆", 39_381.70, 73.67, 0),
        N(NsCategory.InteriorWall, "磚牆雙面粉刷", 33_298.82, 56.28, 0),
        N(NsCategory.IndoorFloor, "貼磁磚地坪", 21_928.64, 33.23, 9.825),
        N(NsCategory.IndoorFloor, "橡膠緩衝墊水泥砂漿+貼地磚", 30_001.85, 35.66, 9.825),
        N(NsCategory.OutdoorFloor, "RC 底+磁磚", 3_316.04, 108.38, 48.28),
    };

    private static void Near(double expected, double actual, double tol = 0.001) =>
        Assert.True(Math.Abs(actual - expected) <= Math.Abs(expected) * tol,
            $"預期 {expected:N2}，實得 {actual:N2}（誤差 {(actual - expected) / expected:P3}）");

    [Fact]
    public void 主結構碳排密度與總量()
    {
        var p = Inputs();
        var d = LebrCalculator.DesignStructure(p);
        var b = LebrCalculator.BaselineStructure(p);
        Near(251.33, d.C);
        Near(294.42, b.C);
        Assert.Equal(1.8, b.Sp);   // 表 8：AFa > 500、9~16F
        Assert.Equal(1.15, b.F);
        Assert.Equal(1.0, b.W);
        Near(12_934_262.81, d.CFs);
        Near(16_563_985.00, b.CFs);
        Near(11_241_564.90, LebrCalculator.UndergroundStructure(p), 1e-5);
    }

    [Fact]
    public void 全案評估結果與手冊一致()
    {
        var r = LebrCalculator.Evaluate(Inputs(), Design(), Baseline());

        Near(7_204_541, r.CFns, 1e-5);
        Near(1_476_954, r.CFrm, 1e-5);
        Near(8_342_492, r.CFnsBaseline, 1e-5);
        Near(1_444_166, r.CFrmBaseline, 1e-5);
        Near(20_138_804, r.CFum);
        Near(24_906_477, r.CFumBaseline);

        Near(811_585.46, r.CFc);
        Near(520_735.46, r.CFcUnderground, 1e-5);   // 手冊值有進位誤差：6.56 × 79,380.29 = 520,734.70
        Near(1_467_375.49, r.CFdw);
        Near(1_274_170.20, r.CFdwUnderground, 1e-5);

        Near(22_756_875, r.EEC);
        Near(28_629_604, r.EECc);
        Near(35_793_345, r.TEC);
        Near(404.50, r.ECI);
        Near(508.88, r.ECIs);
        Assert.InRange(r.CFR, 0.2046, 0.2056);   // 手冊 20.51%
        Assert.Equal("1+級", r.Grade);
    }

    [Fact]
    public void 基準案可由BLCC自動產生()
    {
        var lib = BlccLibrary.Load();
        var design = new List<NonStructuralItem>
        {
            NonStructuralItem.FromBlcc(lib.ById["OW-4"], 100, WearLevel.Low),
            NonStructuralItem.FromBlcc(lib.ById["IW-2"], 50, WearLevel.Low),
            NonStructuralItem.FromBlcc(lib.ById["IW-3"], 30, WearLevel.Low),
            NonStructuralItem.FromBlcc(lib.ById["F-G6"], 20, WearLevel.Low),
            NonStructuralItem.FromBlcc(lib.ById["W-11-PL"], 10, WearLevel.Low),
        };

        var baseline = BaselineBuilder.Build(design, lib.ById, WearLevel.Low);

        Assert.Contains(baseline, x => x.BlccId == "OW-1" && x.Area == 100 && x.NewEf == 28.7);
        Assert.Contains(baseline, x => x.BlccId == "IW-1" && x.Area == 80);       // 兩種隔間合併成磚牆
        Assert.Contains(baseline, x => x.BlccId == "F-G1" && x.RenewEf == 9.825); // 低耗損
        Assert.Contains(baseline, x => x.BlccId == "W-11-AL" && x.NewEf == 48.84); // 同玻璃改鋁框
    }

    [Fact]
    public void BLCC資料庫數值抽查()
    {
        var lib = BlccLibrary.Load();
        Assert.Equal(19.65, lib.ById["OW-4"].New);
        Assert.Equal(53.64, lib.ById["W-12-AL"].New);
        Assert.Equal(73.67, lib.ById["CW-0"].New);
        Assert.Equal(63.66, lib.ById["IW-2"].RenewHigh, 6);
        Assert.Equal(72.53, lib.ById["F-G1"].RenewHigh, 6);
        Assert.Equal(108.38, lib.ById["P-RC1"].New, 6);
        Assert.Equal(48.28, lib.ById["P-RC1"].RenewLow, 6);
        Assert.All(lib.Items, i => Assert.True(lib.ById.ContainsKey(i.BaselineId), $"{i.Id} 的基準 {i.BaselineId} 不存在"));
    }
}

public class LebrTableTests
{
    [Theory]
    [InlineData(3.08, 1.08)]
    [InlineData(1.2, 1.00)]
    [InlineData(1.3, 1.03)]
    [InlineData(1.5, 1.05)]
    public void 表5_f1(double par, double f1) => Assert.Equal(f1, LebrTables.F1(par));

    [Fact]
    public void 周長面積比_手冊實例() =>
        Assert.Equal(3.08, LebrTables.PerimeterAreaRatio(683.4, 3_919.16), 2);

    [Theory]
    [InlineData(0.25, "1+級")]
    [InlineData(0.20, "1級")]
    [InlineData(0.13, "2級")]
    [InlineData(0.05, "4級")]
    [InlineData(0.0, "5級")]
    [InlineData(-0.15, "6級")]
    [InlineData(-0.30, "7級")]
    public void 表10_分級(double cfr, string grade) => Assert.Equal(grade, LebrTables.Grade(cfr));

    [Fact]
    public void 跨距變化係數_手冊實例()
    {
        // 附錄三：X 向 7.3m×6、8.5m×11、4.25m×3；Y 向 9.7m×10、7.65m×7
        var x = Enumerable.Repeat(7.3, 6).Concat(Enumerable.Repeat(8.5, 11)).Concat(Enumerable.Repeat(4.25, 3)).ToList();
        var y = Enumerable.Repeat(9.7, 10).Concat(Enumerable.Repeat(7.65, 7)).ToList();
        Assert.Equal(1.77, SpanCalculator.AxisRatio(x), 2);
        Assert.Equal(1.46, SpanCalculator.Sp(x, y), 2);   // 1.461；手冊文字的 ay 推導有筆誤，但 C 值實際採用 1.461
    }

    [Fact]
    public void 單跨距Sp為1() => Assert.Equal(1.0, SpanCalculator.Sp(new[] { 8.0 }, new[] { 6.0 }));
}
