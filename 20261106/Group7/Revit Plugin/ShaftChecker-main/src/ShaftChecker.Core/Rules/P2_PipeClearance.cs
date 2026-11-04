using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// P2 立管淨距：管道間內的立管兩兩計算中心線距離，扣除兩管半徑與保溫層厚度得到淨距。
    /// </summary>
    public class P2_PipeClearance : RuleBase
    {
        public override string Id { get { return "P2"; } }
        public override string Name { get { return "立管淨距"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.HighRise246Space; } }

        private const int MaxReported = 8;

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            var risers = shaft.Mep.Where(m => ShaftSpace.IsRiserInShaft(shaft, m)).ToList();
            var bad = new List<KeyValuePair<double, MepInfo[]>>();

            for (int i = 0; i < risers.Count; i++)
            {
                for (int j = i + 1; j < risers.Count; j++)
                {
                    double clear = ShaftSpace.RiserClearance(risers[i], risers[j]);
                    if (clear < cfg.MinPipeClearMm - 0.5) // 0.5 mm 以內視為相等，避免「淨距 5 cm 小於 5 cm」
                        bad.Add(new KeyValuePair<double, MepInfo[]>(clear, new[] { risers[i], risers[j] }));
                }
            }

            foreach (var kv in bad.OrderBy(k => k.Key).Take(MaxReported))
            {
                MepInfo a = kv.Value[0], b = kv.Value[1];
                double clear = kv.Key;
                string what = clear < 0 ? "重疊 " + (-clear).ToString("N0") + " mm（模型衝突）" : "淨距 " + clear.ToString("N0") + " mm";
                yield return MakeFor(shaft, Severity.Violation,
                    a.Label + " 與 " + b.Label + " " + what + "（已扣除半徑與保溫 " +
                    (a.InsulationMm + b.InsulationMm).ToString("F0") + " mm），小於 " + cfg.MinPipeClearMm.ToString("F0") + " mm",
                    "將其中一支立管移開至少 " + System.Math.Ceiling(cfg.MinPipeClearMm - clear).ToString("N0") + " mm",
                    clear, cfg.MinPipeClearMm, "mm", a, b);
            }

            if (bad.Count > MaxReported)
                yield return Make(shaft, Severity.Violation,
                    "另有 " + (bad.Count - MaxReported) + " 對立管淨距不足（只列出最嚴重的 " + MaxReported + " 對）");
        }
    }
}
