using System;
using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// P5 防火閘門：風管穿出管道間牆（管道間為防火區劃）的位置，附近須有防火閘門族群
    /// （依族群／類型名稱或「防火閘門」參數判斷）。
    /// </summary>
    public class P5_FireDamper : RuleBase
    {
        public override string Id { get { return "P5"; } }
        public override string Name { get { return "風管穿牆防火閘門"; } }
        public override RuleNature Nature { get { return RuleNature.Regulation; } }
        public override string Reference { get { return LegalRefs.Damper; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (shaft.BoundaryMm.Count < 3) yield break;
            var poly = shaft.BoundaryMm;
            var dampers = shaft.Accessories.Where(a => a.IsFireDamper).ToList();

            foreach (MepInfo m in shaft.Mep.Where(x => x.Kind == MepKind.Duct))
            {
                var p1 = new Point2D(m.X1, m.Y1);
                var p2 = new Point2D(m.X2, m.Y2);
                if (m.IsVertical) continue; // 立管沿管道間上下，不穿管道間牆

                // 中心線與管道間邊界的交點
                double[] cross = null;
                for (int i = 0; i < poly.Count && cross == null; i++)
                {
                    Point2D e1 = poly[i], e2 = poly[(i + 1) % poly.Count];
                    if (!PolygonUtils.SegmentsIntersect(p1, p2, e1, e2)) continue;
                    double t, u;
                    if (!PolygonUtils.LineIntersection(p1, p2, e1, e2, out t, out u)) continue;
                    double z = m.Z1 + t * (m.Z2 - m.Z1);
                    if (!ShaftSpace.InStorey(shaft, z, z)) continue;
                    cross = new[] { m.X1 + t * (m.X2 - m.X1), m.Y1 + t * (m.Y2 - m.Y1), z };
                }
                if (cross == null) continue;

                AccessoryInfo nearest = null;
                double best = double.MaxValue;
                foreach (AccessoryInfo a in dampers)
                {
                    double dx = a.X - cross[0], dy = a.Y - cross[1], dz = a.Z - cross[2];
                    double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    if (dist < best) { best = dist; nearest = a; }
                }
                if (nearest != null && best <= cfg.DamperSearchMm) continue;

                string near = nearest == null ? "附近沒有防火閘門" :
                    "最近的防火閘門「" + nearest.Name + "」距穿牆點 " + best.ToString("N0") + " mm";
                yield return MakeFor(shaft, Severity.Violation,
                    m.Label + " 穿出管道間牆，" + cfg.DamperSearchMm.ToString("N0") + " mm 內找不到防火閘門（" + near + "）",
                    "在穿牆處任一側的風管內加設防火閘門（族群名稱含「防火閘」或勾選「防火閘門」參數）",
                    nearest == null ? (double?)null : best, cfg.DamperSearchMm, "mm", m);
            }
        }
    }
}
