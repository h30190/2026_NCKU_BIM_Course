using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// P1 水電分隔：同一管道間內同時有電氣類（Conduit、Cable Tray）與水類管線時，
    /// 兩群之間每一對元件的連線都必須被管道間內的隔牆截斷，否則判定違規。
    /// </summary>
    public class P1_WaterElectricSeparation : RuleBase
    {
        public override string Id { get { return "P1"; } }
        public override string Name { get { return "電氣與水管分隔"; } }
        public override RuleNature Nature { get { return RuleNature.Regulation; } }
        public override string Reference { get { return LegalRefs.Equip2_1; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            var inShaft = shaft.Mep.Where(m => ShaftSpace.IsInShaft(shaft, m)).ToList();
            var elec = inShaft.Where(MepClassifier.IsElectrical).ToList();
            var water = inShaft.Where(MepClassifier.IsWaterLike).ToList();
            if (elec.Count == 0 || water.Count == 0) yield break;

            var walls = ShaftSpace.PartitionWalls(shaft);
            MepInfo badE = null, badW = null;
            int badPairs = 0;
            double bestDist = double.MaxValue;

            foreach (MepInfo e in elec)
            {
                Point2D pe = ShaftSpace.Representative(shaft, e);
                foreach (MepInfo w in water)
                {
                    Point2D pw = ShaftSpace.Representative(shaft, w);
                    bool separated = walls.Any(wall => PolygonUtils.SegmentsIntersect(pe, pw,
                        new Point2D(wall.X1, wall.Y1), new Point2D(wall.X2, wall.Y2)));
                    if (separated) continue;
                    badPairs++;
                    double dist = System.Math.Sqrt((pe.X - pw.X) * (pe.X - pw.X) + (pe.Y - pw.Y) * (pe.Y - pw.Y));
                    if (dist < bestDist) { bestDist = dist; badE = e; badW = w; }
                }
            }
            if (badPairs == 0) yield break;

            string wallNote = walls.Count == 0 ? "管道間內沒有隔牆" : "管道間內的 " + walls.Count + " 道隔牆未完全隔開兩群";
            yield return MakeFor(shaft, Severity.Violation,
                "同一管道間有電氣類（" + MepClassifier.Summarize(elec) + "）與水類（" + MepClassifier.Summarize(water) +
                "），" + wallNote + "；例如 " + badE.Label + " 與 " + badW.Label + " 之間（相距 " + bestDist.ToString("N0") + " mm）",
                "在兩群之間增設隔牆（或防火隔板），或將電氣管線移至獨立的電氣管道間",
                badPairs, 0, "對", badE, badW);
        }
    }
}
