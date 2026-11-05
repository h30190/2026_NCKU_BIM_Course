using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using ShaftChecker.Core.Models;
using ShaftChecker.Core.Rules;

namespace ShaftChecker.Revit.Writers
{
    /// <summary>
    /// 匯出 CSV 檢討報表（UTF-8 含 BOM，Excel 直接開啟中文不亂碼）。
    /// 預設存到「文件\ShaftChecker\」。
    /// </summary>
    public static class CsvExporter
    {
        private static readonly string[] Header =
        {
            "樓層", "管道間編號", "管道間名稱", "辨識等級", "辨識分數", "辨識依據", "所在模型", "淨寬(mm)", "淨深(mm)", "淨面積(m²)", "地下層", "總結果",
            "分組", "規則", "檢討項目", "性質", "依據", "結果", "說明", "改善建議", "實際值", "門檻", "單位", "相關元件Id"
        };

        public static string Export(string modelTitle, CheckResult result)
        {
            string folder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ShaftChecker");
            Directory.CreateDirectory(folder);

            string safeTitle = string.Concat((modelTitle ?? "model").Split(Path.GetInvalidFileNameChars()));
            string path = Path.Combine(folder,
                safeTitle + "_管道間檢討_" + DateTime.Now.ToString("yyyyMMdd_HHmm") + ".csv");

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(",", Header.Select(Escape)));

            foreach (ShaftResult sr in result.Shafts)
            {
                ShaftInfo s = sr.Shaft;
                foreach (Issue i in sr.Issues)
                {
                    var row = new List<string>
                    {
                        s.LevelName,
                        s.Number,
                        s.Name,
                        s.Detection != null ? s.Detection.LevelLabel : "",
                        s.Detection != null ? s.Detection.Score.ToString(CultureInfo.InvariantCulture) : "",
                        s.Detection != null ? string.Join("；", s.Detection.Reasons) : "",
                        s.SourceName ?? "（主模型）",
                        s.ClearWidthMm.ToString("F0", CultureInfo.InvariantCulture),
                        s.ClearDepthMm.ToString("F0", CultureInfo.InvariantCulture),
                        s.AreaM2.ToString("F2", CultureInfo.InvariantCulture),
                        s.IsUnderground ? "是" : "",
                        Labels.Of(sr.Overall),
                        RuleGroups.Label(RuleGroups.Of(i.RuleId)),
                        i.RuleId,
                        i.RuleName,
                        Labels.Of(i.Nature),
                        i.Reference,
                        Labels.Of(i.Severity),
                        i.Message,
                        i.Suggestion ?? "",
                        Num(i.Actual),
                        Num(i.Required),
                        i.Unit,
                        string.Join(" ", i.ElementIds)
                    };
                    sb.AppendLine(string.Join(",", row.Select(Escape)));
                }
            }

            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
            return path;
        }

        private static string Num(double? v)
        {
            return v.HasValue ? v.Value.ToString("0.##", CultureInfo.InvariantCulture) : "";
        }

        private static string Escape(string v)
        {
            if (v == null) return "";
            if (v.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
                return "\"" + v.Replace("\"", "\"\"") + "\"";
            return v;
        }
    }
}
