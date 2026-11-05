using System.Globalization;
using System.Text.RegularExpressions;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// 把 Revit「防火時效 / Fire Rating」文字參數轉成小時數。
    /// 支援：「1」「1 hr」「1小時」「1h」「60 min」「60分」「一小時」「兩小時」。
    /// 無法辨識時回傳 null（判定為資料不足）。
    /// </summary>
    public static class FireRatingParser
    {
        private static readonly Regex Number = new Regex(@"(\d+(\.\d+)?)");

        public static double? ParseHours(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return null;
            string s = raw.Trim().ToLowerInvariant();

            double value;
            Match m = Number.Match(s);
            if (m.Success)
            {
                value = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            else
            {
                double? zh = ChineseNumeral(s);
                if (!zh.HasValue) return null;
                value = zh.Value;
            }

            bool minutes = s.Contains("min") || s.Contains("分");
            return minutes ? value / 60.0 : value;
        }

        private static double? ChineseNumeral(string s)
        {
            if (s.Contains("半")) return 0.5;
            if (s.Contains("一")) return 1;
            if (s.Contains("二") || s.Contains("兩")) return 2;
            if (s.Contains("三")) return 3;
            if (s.Contains("四")) return 4;
            return null;
        }
    }
}
