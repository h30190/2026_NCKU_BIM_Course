using System.Globalization;
using System.Text;
using LebrCarbon.Core.Models;

namespace LebrCarbon.Reports;

/// <summary>把計算明細輸出成 CSV（UTF-8 BOM，Excel 可直接開啟中文）。</summary>
public static class CsvReportWriter
{
    public static void Write(CalcResult r, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("元件ID,類別,材料,數量,單位,係數ID,係數,階段,係數來源,碳排kgCO2e");
        foreach (var l in r.Lines)
            sb.AppendLine(string.Join(",",
                l.ElementId, Esc(l.Category), Esc(l.MaterialName), F(l.Quantity), l.Unit,
                l.FactorId, F(l.Ef), l.Stage, Esc(l.FactorSource), F(l.Emission)));
        File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
    }

    private static string F(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
    private static string Esc(string s) => s.Contains(',') || s.Contains('"') ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
}
