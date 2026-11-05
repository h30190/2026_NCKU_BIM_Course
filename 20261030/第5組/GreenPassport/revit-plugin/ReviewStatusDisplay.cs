using System.Drawing;

namespace TemporalBim
{
    public sealed class ReviewStatusOption
    {
        public string Code { get; set; }
        public string Label { get; set; }
    }

    public static class ReviewStatusDisplay
    {
        public static ReviewStatusOption[] Options => new[]
        {
            new ReviewStatusOption { Code = "Pending", Label = "待審核" },
            new ReviewStatusOption { Code = "Approved", Label = "已通過審核" },
            new ReviewStatusOption { Code = "Repair Required", Label = "需修復" },
            new ReviewStatusOption { Code = "Rejected", Label = "未通過審核" },
        };

        public static string Label(string code)
        {
            foreach (var option in Options) if (option.Code == code) return option.Label;
            return string.IsNullOrWhiteSpace(code) ? "待審核" : code;
        }

        public static Color Background(string code) => code == "Approved" ? Color.Honeydew : code == "Rejected" ? Color.MistyRose : Color.LemonChiffon;
        public static Color Foreground(string code) => code == "Approved" ? Color.DarkGreen : code == "Rejected" ? Color.DarkRed : Color.FromArgb(110, 75, 0);
    }
}
