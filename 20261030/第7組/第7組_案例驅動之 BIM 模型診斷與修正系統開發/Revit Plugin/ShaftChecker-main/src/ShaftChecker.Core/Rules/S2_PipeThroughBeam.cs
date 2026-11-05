using System.Collections.Generic;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// S2 管線穿梁：水平管線與梁的衝突。完整穿越梁腹時檢查孔徑、孔位、距支承面、孔距；
    /// 管線局部撞梁或沿梁內部配置，多半是標高畫錯，提出下移距離。
    /// </summary>
    public class S2_PipeThroughBeam : RuleBase
    {
        public override string Id { get { return "S2"; } }
        public override string Name { get { return "管線穿梁"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.BeamHole; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (!shaft.StructureModelFound) yield break; // S1 已提示找不到結構模型

            foreach (BeamHit h in BeamPenetration.Analyze(shaft, cfg))
            {
                string head = h.Parallel ? h.Mep.Label + " 與" + h.Beam.Label :
                              h.Through ? h.Mep.Label + " 穿過" + h.Beam.Label + "（孔徑 " + h.HoleMm.ToString("N0") +
                                          " mm，梁深 " + h.Beam.DepthMm.ToString("N0") + " mm）"
                                        : h.Mep.Label + " 與" + h.Beam.Label;
                yield return MakeFor(shaft, Severity.Violation,
                    head + "：" + string.Join("；", h.Problems),
                    h.Suggestion, null, null, null, h.Mep, h.Beam);
            }
        }
    }
}
