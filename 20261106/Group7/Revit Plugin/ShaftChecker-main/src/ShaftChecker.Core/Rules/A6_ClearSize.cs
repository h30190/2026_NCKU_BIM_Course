using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// A6 管道間淨尺寸：以完成面邊界計算最小外接矩形，短邊為淨寬、長邊為淨深；面積取 Room.Area。
    /// </summary>
    public class A6_ClearSize : RuleBase
    {
        public override string Id { get { return "A6"; } }
        public override string Name { get { return "管道間淨尺寸"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.HighRise246Space; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (shaft.BoundaryMm.Count < 3)
            {
                yield return Make(shaft, Severity.DataMissing, "無法取得管道間完成面邊界，請確認 Room 已正確封閉");
                yield break;
            }

            shaft.EnsureMetrics();
            long[] wallIds = shaft.Walls.Select(w => w.Id).ToArray();

            if (shaft.ClearWidthMm < cfg.MinClearWidthMm)
            {
                yield return Make(shaft, Severity.Violation,
                    "淨寬（最小外接矩形短邊）" + shaft.ClearWidthMm.ToString("F0") + " mm，小於 " + cfg.MinClearWidthMm.ToString("F0") + " mm",
                    "淨寬需再增加 " + (cfg.MinClearWidthMm - shaft.ClearWidthMm).ToString("N0") + " mm",
                    shaft.ClearWidthMm, cfg.MinClearWidthMm, "mm", wallIds);
            }

            if (cfg.MinClearDepthMm > 0 && shaft.ClearDepthMm < cfg.MinClearDepthMm)
            {
                yield return Make(shaft, Severity.Violation,
                    "淨深（最小外接矩形長邊）" + shaft.ClearDepthMm.ToString("F0") + " mm，小於 " + cfg.MinClearDepthMm.ToString("F0") + " mm",
                    "淨深需再增加 " + (cfg.MinClearDepthMm - shaft.ClearDepthMm).ToString("N0") + " mm",
                    shaft.ClearDepthMm, cfg.MinClearDepthMm, "mm", wallIds);
            }

            if (cfg.MinClearAreaM2 > 0 && shaft.AreaM2 < cfg.MinClearAreaM2)
            {
                yield return Make(shaft, Severity.Violation,
                    "淨面積（Room.Area）" + shaft.AreaM2.ToString("F2") + " m²，小於 " + cfg.MinClearAreaM2.ToString("F2") + " m²",
                    shaft.AreaM2, cfg.MinClearAreaM2, "m²", wallIds);
            }
        }
    }
}
