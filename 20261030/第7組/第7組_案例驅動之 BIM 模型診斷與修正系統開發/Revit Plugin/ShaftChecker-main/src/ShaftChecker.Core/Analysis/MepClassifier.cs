using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Analysis
{
    /// <summary>
    /// 依元件種類與系統文字（系統分類、系統類型、系統名稱、管類型）把管線分群。
    /// 電管、電纜架一律為電氣類；風管為風管類；水管依關鍵字判斷，順序為 瓦斯 → 消防 → 空調水 → 給排水。
    /// </summary>
    public static class MepClassifier
    {
        public static MepGroup Classify(MepKind kind, string systemText, RuleConfig cfg)
        {
            switch (kind)
            {
                case MepKind.Conduit:
                case MepKind.CableTray:
                    return MepGroup.Electrical;
                case MepKind.Duct:
                    return MepGroup.Air;
            }

            string t = systemText ?? "";
            if (RuleConfig.MatchesAny(t, cfg.GasSystemPatterns)) return MepGroup.Gas;
            if (RuleConfig.MatchesAny(t, cfg.FireSystemPatterns)) return MepGroup.FireProtection;
            if (RuleConfig.MatchesAny(t, cfg.HvacWaterPatterns)) return MepGroup.HvacWater;
            if (RuleConfig.MatchesAny(t, cfg.PlumbingPatterns)) return MepGroup.Plumbing;
            return MepGroup.OtherPipe;
        }

        /// <summary>設定檔中「可與瓦斯管同在一個管道間」的分群。</summary>
        public static HashSet<MepGroup> GasAllowed(RuleConfig cfg)
        {
            var set = new HashSet<MepGroup> { MepGroup.Gas };
            foreach (string label in cfg.GasAllowedGroups ?? new List<string>())
            {
                MepGroup? g = MepLabels.Parse(label);
                if (g.HasValue) set.Add(g.Value);
            }
            return set;
        }

        /// <summary>水類（建築設備編第 2-1 條要與電氣管道分隔的管線）：所有水管與瓦斯管。</summary>
        public static bool IsWaterLike(MepInfo m)
        {
            return m.Kind == MepKind.Pipe;
        }

        public static bool IsElectrical(MepInfo m)
        {
            return m.Kind == MepKind.Conduit || m.Kind == MepKind.CableTray;
        }

        public static string Summarize(IEnumerable<MepInfo> list)
        {
            return string.Join("、", list.GroupBy(m => MepLabels.Of(m.Group))
                .Select(g => g.Key + " " + g.Count() + " 條"));
        }
    }
}
