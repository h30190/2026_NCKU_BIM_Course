using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ShaftChecker.Core.Config;

namespace ShaftChecker.Core.Recognition
{
    /// <summary>
    /// 多訊號判斷「這個空間是不是管道間」。不再只靠名稱：
    ///   名稱／編號符合 +100、房間參數提到管道間 +60、Shaft Opening 貫穿 +40、
    ///   內有機電元件（最多 +40）、尺寸小而窄（最多 +35）、上下樓層位置相疊（最多 +20）；
    ///   名稱明顯是別的空間 −60、門太多 −15、面積太大則封頂在「非管道間」。
    /// 分數對應等級：≥確定門檻 確定；≥很可能門檻 很可能；≥疑似門檻 疑似；其餘非管道間。
    /// 沒有任何文字證據（名稱、編號、參數）時，最高只會判成「很可能」。
    /// 使用者明確標「是」「否」時，直接採用使用者的決定。
    /// </summary>
    public static class ShaftClassifier
    {
        // 權重集中在這裡，方便調整與寫進報告
        public const int NameMatchPoints = 100;
        public const int ParameterPoints = 60;
        public const int ShaftOpeningPoints = 40;
        public const int NonShaftNamePenalty = -60;
        public const int ManyDoorsPenalty = -15;
        public const int MepCap = 40;
        public const int StackTwoOrMorePoints = 20;
        public const int StackOnePoints = 10;

        private static readonly Regex GenericName = new Regex(
            @"^\s*(room|space|房間|空間|未命名|未定義)?\s*\d*\s*$", RegexOptions.IgnoreCase);

        public static ShaftDetection Classify(SpaceFeatures f, RuleConfig cfg)
        {
            var d = new ShaftDetection();

            // 使用者決定優先
            string choice = NormalizeChoice(f.UserConfirm);
            if (choice == "否")
            {
                d.ByUser = true;
                d.ExcludedByUser = true;
                d.Level = DetectionLevel.None;
                d.Reasons.Add("使用者標記為「不是管道間」");
                return d;
            }
            if (choice == "是")
            {
                d.ByUser = true;
                d.HasTextEvidence = true;
                d.Score = 200;
                d.Level = DetectionLevel.Confirmed;
                d.Reasons.Add("使用者確認為管道間");
                return d;
            }

            int score = 0;
            string name = f.Name ?? "";

            // 1. 名稱／編號
            bool nameHit = RuleConfig.MatchesAny(f.Name, cfg.ShaftNamePatterns) ||
                           RuleConfig.MatchesAny(f.Number, cfg.ShaftNamePatterns);
            if (nameHit)
            {
                score += NameMatchPoints;
                d.HasTextEvidence = true;
                d.Reasons.Add("名稱／編號（" + Describe(f) + "）符合管道間命名");
            }
            else if (RuleConfig.MatchesAny(f.Name, cfg.NonShaftNamePatterns))
            {
                score += NonShaftNamePenalty;
                d.Reasons.Add("名稱「" + name + "」看起來是其他空間（扣分）");
            }
            else if (GenericName.IsMatch(name))
            {
                d.Reasons.Add("名稱沒有說明用途（" + (string.IsNullOrWhiteSpace(name) ? "未命名" : name) + "），改看其他線索");
            }

            // 2. 房間參數文字
            if (!string.IsNullOrEmpty(f.ParameterHit))
            {
                score += ParameterPoints;
                d.HasTextEvidence = true;
                d.Reasons.Add("房間參數提到管道間：" + f.ParameterHit);
            }

            // 3. Shaft Opening
            if (f.ShaftOpeningCount > 0)
            {
                score += ShaftOpeningPoints;
                d.Reasons.Add("有 " + f.ShaftOpeningCount + " 個 Shaft Opening（垂直開口）貫穿此位置");
            }

            // 4. 內含機電元件
            int mep = 0;
            if (f.MepLinearCount >= 3) mep += 25;
            else if (f.MepLinearCount >= 1) mep += 15;
            if (f.MepVerticalCount >= 1) mep += 15;
            if (f.MepEquipmentCount >= 1) mep += 5;
            if (mep > MepCap) mep = MepCap;
            if (mep > 0)
            {
                score += mep;
                d.Reasons.Add("內有機電元件：管線 " + f.MepLinearCount + " 條（垂直 " + f.MepVerticalCount +
                              " 條）、設備 " + f.MepEquipmentCount + " 件");
            }

            // 5. 尺寸小而窄
            int geo = 0;
            if (f.AreaM2 > 0)
            {
                if (f.AreaM2 <= 4) geo += 25;
                else if (f.AreaM2 <= 8) geo += 12;
                if (f.AreaM2 <= 8 && f.ShortSideMm > 0 && f.ShortSideMm <= 1200) geo += 10;
            }
            if (geo > 0)
            {
                score += geo;
                d.Reasons.Add("尺寸小而窄：面積 " + f.AreaM2.ToString("F1") + " m²，短邊 " + f.ShortSideMm.ToString("F0") + " mm");
            }

            // 6. 上下樓層位置相疊（貫穿樓層的特徵）
            if (f.AreaM2 > 0 && f.AreaM2 <= cfg.MaxShaftAreaM2)
            {
                if (f.StackedLevels >= 2)
                {
                    score += StackTwoOrMorePoints;
                    d.Reasons.Add("其他 " + f.StackedLevels + " 個樓層在相同位置有相近大小的房間（貫穿樓層）");
                }
                else if (f.StackedLevels == 1)
                {
                    score += StackOnePoints;
                    d.Reasons.Add("另一個樓層在相同位置有相近大小的房間");
                }
            }

            // 7. 扣分項
            if (f.DoorCount >= 3)
            {
                score += ManyDoorsPenalty;
                d.Reasons.Add("有 " + f.DoorCount + " 扇門，像一般房間（扣分）");
            }

            if (f.AreaM2 > cfg.MaxShaftAreaM2 && !nameHit)
            {
                int cap = cfg.DetectCandidateScore - 1;
                if (score > cap) score = cap;
                d.Reasons.Add("面積 " + f.AreaM2.ToString("F1") + " m² 超過管道間上限 " + cfg.MaxShaftAreaM2.ToString("F0") + " m²");
            }

            d.Score = score;
            d.Level = LevelOf(score, cfg);

            // 沒有文字證據，只靠推測，最高到「很可能」
            if (!d.HasTextEvidence && d.Level == DetectionLevel.Confirmed)
                d.Level = DetectionLevel.Likely;

            return d;
        }

        public static DetectionLevel LevelOf(int score, RuleConfig cfg)
        {
            if (score >= cfg.DetectConfirmedScore) return DetectionLevel.Confirmed;
            if (score >= cfg.DetectLikelyScore) return DetectionLevel.Likely;
            if (score >= cfg.DetectCandidateScore) return DetectionLevel.Candidate;
            return DetectionLevel.None;
        }

        /// <summary>
        /// 計算每個小空間在其他樓層「相同位置、相近大小」的房間數，寫入 StackedLevels。
        /// 重心距離在容許值內、面積比在範圍內才算疊在一起。
        /// </summary>
        public static void AssignStacks(IList<SpaceFeatures> all, double maxAreaM2,
            double centroidTolMm = 400, double minRatio = 0.7, double maxRatio = 1.4)
        {
            var small = all.Where(f => f.AreaM2 > 0 && f.AreaM2 <= maxAreaM2).ToList();
            double tol2 = centroidTolMm * centroidTolMm;

            foreach (var f in small)
            {
                var levels = new HashSet<string>();
                foreach (var g in small)
                {
                    if (ReferenceEquals(f, g)) continue;
                    if (string.Equals(g.LevelName, f.LevelName, StringComparison.Ordinal)) continue;

                    double dx = g.CentroidXMm - f.CentroidXMm;
                    double dy = g.CentroidYMm - f.CentroidYMm;
                    if (dx * dx + dy * dy > tol2) continue;

                    double ratio = g.AreaM2 / f.AreaM2;
                    if (ratio < minRatio || ratio > maxRatio) continue;

                    levels.Add(g.LevelName ?? "");
                }
                f.StackedLevels = levels.Count;
            }
        }

        private static string NormalizeChoice(string value)
        {
            string v = (value ?? "").Trim().ToUpperInvariant();
            if (v == "是" || v == "Y" || v == "YES" || v == "TRUE" || v == "1") return "是";
            if (v == "否" || v == "N" || v == "NO" || v == "FALSE" || v == "0") return "否";
            return null;
        }

        private static string Describe(SpaceFeatures f)
        {
            string number = string.IsNullOrWhiteSpace(f.Number) ? "" : f.Number;
            string name = string.IsNullOrWhiteSpace(f.Name) ? "" : f.Name;
            return (number + " " + name).Trim();
        }
    }
}
