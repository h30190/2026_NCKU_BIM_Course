using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// S1 管道間與柱梁：
    /// 1. 梁、柱的平面投影侵入管道間（梁投影視為禁區），計算遮擋面積、梁底到樓板淨高、扣除後的可用淨尺寸；
    /// 2. 立管穿過梁或柱（建築平面看不到梁，管線常被畫到梁裡）。
    /// 違規時以管道間邊界與柱梁投影做 2D 差集，提出管道間或立管可移動的方向與最小距離。
    /// </summary>
    public class S1_ShaftStructure : RuleBase
    {
        public override string Id { get { return "S1"; } }
        public override string Name { get { return "管道間避開柱梁"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.StructCoord; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (!shaft.StructureModelFound)
            {
                yield return Make(shaft, Severity.Warning,
                    "專案（含連結）中找不到結構梁柱，無法檢討；請確認結構模型已連結並載入");
                yield break;
            }

            StructureReport rep = StructureAnalysis.Analyze(shaft, cfg);
            var blocking = rep.Blocking;

            if (blocking.Count > 0)
            {
                string moves = rep.ShaftMoves.Count > 0
                    ? "管道間可" + string.Join("，或", rep.ShaftMoves.Select(m => m.Text)) + " 完全避開柱梁"
                    : "在 " + cfg.MaxMoveSearchMm.ToString("N0") + " mm 內找不到可完全避開柱梁的位置，需與結構協調";
                string free = "扣除柱梁後可用 " + rep.FreeAreaM2.ToString("F2") + " m²（約 " + rep.FreeWidthMm.ToString("N0") +
                              " mm × " + rep.FreeDepthMm.ToString("N0") + " mm）";

                foreach (MemberOverlap m in blocking)
                {
                    string height = m.IsBeam && m.ClearHeightMm.HasValue
                        ? "，梁底至本層樓板淨高 " + m.ClearHeightMm.Value.ToString("N0") + " mm"
                        : "";
                    double pct = rep.ShaftAreaM2 > 0 ? m.OverlapAreaM2 / rep.ShaftAreaM2 * 100 : 0;
                    yield return MakeFor(shaft, Severity.Violation,
                        m.Label + (m.IsBeam ? "穿越管道間" : "侵入管道間") + "：遮擋 " + m.OverlapAreaM2.ToString("F2") +
                        " m²（" + pct.ToString("F0") + "%），侵入深度 " + m.OverlapDepthMm.ToString("N0") + " mm" + height,
                        free + "；" + moves,
                        m.OverlapAreaM2, 0, "m²", m.Member);
                }

                if (rep.FreeWidthMm < cfg.MinClearWidthMm)
                    yield return Make(shaft, Severity.Violation,
                        "扣除柱梁投影後，管道間最大可用淨寬只剩 " + rep.FreeWidthMm.ToString("N0") + " mm，小於 " +
                        cfg.MinClearWidthMm.ToString("N0") + " mm", moves,
                        rep.FreeWidthMm, cfg.MinClearWidthMm, "mm");
            }

            foreach (RiserConflict r in rep.Risers)
            {
                string move = r.Move != null
                    ? "立管可在管道間內" + r.Move.Text + "避開"
                    : "管道間內沒有可避開的位置，需移動管道間或與結構協調";
                yield return MakeFor(shaft, Severity.Violation,
                    r.Mep.Label + " 穿過" + r.MemberLabel + "（標高 " + r.Z0.ToString("N0") + " mm～" + r.Z1.ToString("N0") +
                    " mm 重疊；建築平面看不到梁，立管可能畫錯位置）",
                    move, null, null, null, r.Mep, r.Member);
            }
        }
    }
}
