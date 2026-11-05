using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// P3 維修側淨空：維修門正後方（管道間內側，門寬 × 指定深度、門高範圍內）不得有管線佔據。
    /// </summary>
    public class P3_DoorBackClearance : RuleBase
    {
        public override string Id { get { return "P3"; } }
        public override string Name { get { return "維修門後方淨空"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.HighRise246Space; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (shaft.Mep.Count == 0) yield break; // 附近沒有管線

            foreach (DoorInfo d in shaft.Doors)
            {
                if (!d.HasGeometry)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "維修門「" + d.Label + "」無法取得位置，不能檢討門後方淨空", null, null, null, d.Id);
                    continue;
                }

                var zone = ShaftSpace.DoorBackZone(d, cfg.DoorBackClearDepthMm);
                double z0 = d.BaseZ, z1 = d.BaseZ + (d.HeightMm > 0 ? d.HeightMm : cfg.MinDoorHeightMm);
                var hits = new List<MepInfo>();

                foreach (MepInfo m in shaft.Mep)
                {
                    if (m.IsVertical)
                    {
                        if (m.MaxZ < z0 || m.MinZ > z1) continue;
                        if (ShaftSpace.RiserTouches(m, zone)) hits.Add(m);
                    }
                    else
                    {
                        double rv = m.VerticalSizeMm / 2;
                        if (m.MaxZ + rv < z0 || m.MinZ - rv > z1) continue;
                        double dist = ShaftSpace.SegmentPolygonDistance(new Point2D(m.X1, m.Y1), new Point2D(m.X2, m.Y2), zone);
                        if (dist < m.OuterRadiusMm) hits.Add(m);
                    }
                }
                if (hits.Count == 0) continue;

                var ids = new List<object> { d };
                ids.AddRange(hits);
                yield return MakeFor(shaft, Severity.Violation,
                    "維修門「" + d.Label + "」正後方 " + cfg.DoorBackClearDepthMm.ToString("N0") + " mm 範圍內有 " + hits.Count +
                    " 條管線：" + string.Join("、", hits.Take(4).Select(h => h.Label)) + (hits.Count > 4 ? "…" : ""),
                    "將管線移離門後方作業區，或將維修門移到管線較少的一側",
                    hits.Count, 0, "條", ids.ToArray());
            }
        }
    }
}
