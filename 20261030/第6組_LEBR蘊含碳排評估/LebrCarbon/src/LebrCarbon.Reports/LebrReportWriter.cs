using System.Globalization;
using System.Text;
using LebrCarbon.Core.Lebr;

namespace LebrCarbon.Reports;

/// <summary>LEBR 評估結果的文字報告與 CSV 明細（格式參照手冊附錄三）。</summary>
public static class LebrReportWriter
{
    private static string N(double v) => v.ToString("N0", CultureInfo.InvariantCulture);
    private static string D(double v, int d = 2) => v.ToString("N" + d, CultureInfo.InvariantCulture);

    public static string ToText(LebrResult r, string? shapeNote = null)
    {
        var p = r.Inputs;
        var sb = new StringBuilder();
        sb.AppendLine($"LEBR 評估結果　等級 {r.Grade}　碳排減碳率 CFR = {r.CFR:P2}");
        sb.AppendLine("（設計自主檢討用，非正式標示結果）");
        sb.AppendLine();
        sb.AppendLine($"評估範疇蘊含碳排 EEC   {N(r.EEC),16} kgCO2e");
        sb.AppendLine($"基準案 EECc            {N(r.EECc),16} kgCO2e");
        sb.AppendLine($"碳排總減碳量 ΔCF       {N(r.DeltaCF),16} kgCO2e");
        sb.AppendLine($"設計案碳排密度 ECI     {D(r.ECI),16} kgCO2e/m2");
        sb.AppendLine($"基準案碳排密度 ECIs    {D(r.ECIs),16} kgCO2e/m2");
        sb.AppendLine($"全生命週期 TEC         {N(r.TEC),16} kgCO2e（含地下層，不列入分級）");
        sb.AppendLine();

        sb.AppendLine("■ 主結構（式 1 ~ 4）");
        sb.AppendLine($"  AFu {D(p.AFu)} m2　AFb {D(p.AFb)} m2　S {p.S:0.##}　Sb {p.Sb:0.##}　BH {p.BH:0.##} m");
        sb.AppendLine($"  I {p.I}　SaD {p.SaD}　Fu {p.Fu}　Rs {p.Rs}　L {p.L}　D0 {p.D0}");
        sb.AppendLine($"                     設計案        基準案");
        sb.AppendLine($"  構造係數 W     {r.Design.W,10:0.00}    {r.Baseline.W,10:0.00}");
        sb.AppendLine($"  跨距變化 Sp    {r.Design.Sp,10:0.000}    {r.Baseline.Sp,10:0.000}");
        sb.AppendLine($"  形狀係數 F     {r.Design.F,10:0.000}    {r.Baseline.F,10:0.000}");
        sb.AppendLine($"  LCCR × RN      {r.Design.LCCR * r.Design.RN,10:0.000}    {1.0,10:0.000}");
        sb.AppendLine($"  碳排密度 C     {r.Design.C,10:0.00}    {r.Baseline.C,10:0.00}  kgCO2e/m2");
        sb.AppendLine($"  主結構 CFs     {N(r.Design.CFs),14}    {N(r.Baseline.CFs),14}");
        if (r.Design.CBeforeFloor < LebrCalculator.MinC)
            sb.AppendLine($"  ※ 設計案 C 計算值 {r.Design.CBeforeFloor:0.00} 低於下限，依式 4 以 165 計");
        if (!string.IsNullOrEmpty(shapeNote)) sb.AppendLine($"  {shapeNote}");
        sb.AppendLine();

        sb.AppendLine("■ 非主結構（式 11）              設計案新建     設計案更新     基準案新建     基準案更新");
        foreach (var c in Enum.GetValues<NsCategory>())
        {
            var d = r.DesignSubtotals.First(s => s.Category == c);
            var b = r.BaselineSubtotals.First(s => s.Category == c);
            sb.AppendLine($"  {Pad(c.DisplayName(), 26)}{N(d.New),14} {N(d.Renew),14} {N(b.New),14} {N(b.Renew),14}");
        }
        sb.AppendLine($"  {Pad("小計", 26)}{N(r.CFns),14} {N(r.CFrm),14} {N(r.CFnsBaseline),14} {N(r.CFrmBaseline),14}");
        sb.AppendLine();

        sb.AppendLine("■ 生命週期四階段（地上層）          設計案          基準案");
        sb.AppendLine($"  製造運輸 CFum                {N(r.CFum),14}  {N(r.CFumBaseline),14}");
        sb.AppendLine($"  更新修繕 CFrm                {N(r.CFrm),14}  {N(r.CFrmBaseline),14}");
        sb.AppendLine($"  施工 CFc                     {N(r.CFc),14}  {N(r.CFc),14}");
        sb.AppendLine($"  拆除廢棄 CFdw（Wd {r.Wd}）      {N(r.CFdw),14}  {N(r.CFdw),14}");
        var credits = p.ReuseCredit + p.RecycledMaterialCredit + p.LowCarbonMethodCredit;
        if (credits > 0) sb.AppendLine($"  減碳優惠（舊建材、循環建材、工法） −{N(credits)}");
        if (p.LL > 0) sb.AppendLine($"  建築延壽優惠 LL = {p.LL}（EEC 除以 {1 + p.LL}）");
        sb.AppendLine();
        sb.AppendLine("■ 地下層（不列入分級）");
        sb.AppendLine($"  主結構 CFs' {N(r.CFsUnderground)}　施工 CFc' {N(r.CFcUnderground)}　拆除廢棄 CFdw' {N(r.CFdwUnderground)}");
        sb.AppendLine();
        sb.AppendLine("■ 八等級刻度（表 12，kgCO2e/m2）");
        sb.AppendLine("  " + string.Join("　", r.Scale.Select(t => $"{t.Grade} {t.Eci:0}")));
        sb.AppendLine($"  本案 ECI {r.ECI:0.00}");
        return sb.ToString();
    }

    /// <summary>全形字佔兩格的補空白。</summary>
    private static string Pad(string s, int width)
    {
        var w = s.Sum(ch => ch > 0x2E80 ? 2 : 1);
        return s + new string(' ', Math.Max(1, width - w));
    }

    public static void WriteCsv(LebrResult r, string path, string? shapeNote = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\"" + ToText(r, shapeNote).Replace("\"", "\"\"") + "\"");
        sb.AppendLine();
        sb.AppendLine("情境,類別,構件,B-LCC ID,面積m2,新建碳排標準,更新碳排標準,新建碳排kgCO2e,更新碳排kgCO2e,來源");
        void Rows(string scenario, IEnumerable<NonStructuralItem> items)
        {
            foreach (var i in items)
                sb.AppendLine(string.Join(",", scenario, i.Category.DisplayName(), Esc(i.Name), i.BlccId ?? "自訂",
                    F(i.Area), F(i.NewEf), F(i.RenewEf), F(i.NewEmission), F(i.RenewEmission), Esc(i.Source)));
        }
        Rows("設計案", r.DesignItems);
        Rows("基準案", r.BaselineItems);
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
