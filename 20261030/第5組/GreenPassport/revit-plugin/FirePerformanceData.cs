using System;
using System.Text.RegularExpressions;

namespace TemporalBim
{
    /// <summary>Explicit no-fire declarations are a completed zero, never a missing value.</summary>
    public static class FirePerformanceData
    {
        public static bool IsExplicitZero(string rating)
        {
            string text = (rating ?? "").Trim();
            if (text.Length == 0) return false;
            foreach (string label in new[] { "無防火需求", "不要求防火", "無防火性能", "不具防火性能", "無防火時效", "non-rated", "no fire" })
                if (text.IndexOf(label, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return string.Equals(text, "none", StringComparison.OrdinalIgnoreCase) ||
                Regex.IsMatch(text, @"^0+(?:\.0+)?\s*(?:分鐘|分钟|分|minutes?|mins?|min|小時|小时|hours?|hrs?|hr|h)?$", RegexOptions.IgnoreCase);
        }

        public static int ResolveMinutes(string rating, int minutes) =>
            IsExplicitZero(rating) ? 0 : minutes > 0 ? minutes : DoorDataExtractor.ParseFireMinutes(rating);

        public static bool IsComplete(string rating, int minutes) =>
            !string.IsNullOrWhiteSpace(rating) && (IsExplicitZero(rating) || ResolveMinutes(rating, minutes) > 0);

        public static string DisplayMinutes(string rating, int minutes)
        {
            if (IsExplicitZero(rating)) return "0 分鐘";
            int resolved = ResolveMinutes(rating, minutes);
            return resolved > 0 ? resolved + " 分鐘" : "待確認";
        }

        public static void Normalize(PassportInfo passport)
        {
            if (!IsExplicitZero(passport.FireRating)) return;
            passport.FireRating = "0";
            passport.FireMinutes = 0;
        }
    }
}
