using System;
using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Analysis
{
    /// <summary>管道間與元件之間的平面幾何判斷（全部用主模型 mm 座標）。</summary>
    public static class ShaftSpace
    {
        /// <summary>是否知道本層樓板與上一層樓板標高。</summary>
        public static bool HasStorey(ShaftInfo s)
        {
            return s.CeilingZ > s.FloorZ + 1;
        }

        /// <summary>z 範圍 [z0, z1] 是否與本層（樓板到上一層樓板）重疊；不知道樓層高度時一律視為重疊。</summary>
        public static bool InStorey(ShaftInfo s, double z0, double z1, double margin = 0)
        {
            if (!HasStorey(s)) return true;
            return z1 > s.FloorZ + margin && z0 < s.CeilingZ - margin;
        }

        /// <summary>梁屬於本層：梁頂高於本層樓板、梁底低於上一層樓板。</summary>
        public static bool BeamInStorey(ShaftInfo s, BeamInfo b)
        {
            if (!HasStorey(s)) return true;
            return b.TopZ > s.FloorZ + 100 && b.BottomZ < s.CeilingZ - 1;
        }

        public static bool ColumnInStorey(ShaftInfo s, ColumnInfo c)
        {
            return InStorey(s, c.BottomZ, c.TopZ, 50);
        }

        /// <summary>立管中心是否在管道間內、而且在本層範圍內。</summary>
        public static bool IsRiserInShaft(ShaftInfo s, MepInfo m)
        {
            return m.IsVertical && PolygonUtils.Contains(s.BoundaryMm, m.X1, m.Y1) && InStorey(s, m.MinZ, m.MaxZ);
        }

        /// <summary>中心線沿線取樣點（含兩端）。</summary>
        public static IEnumerable<double[]> Samples(MepInfo m, int n = 24)
        {
            for (int i = 0; i <= n; i++)
            {
                double t = (double)i / n;
                yield return new[]
                {
                    m.X1 + t * (m.X2 - m.X1), m.Y1 + t * (m.Y2 - m.Y1), m.Z1 + t * (m.Z2 - m.Z1)
                };
            }
        }

        /// <summary>元件是否有一部分位於管道間（平面在邊界內，標高在本層內）。</summary>
        public static bool IsInShaft(ShaftInfo s, MepInfo m)
        {
            if (m.IsVertical) return IsRiserInShaft(s, m);
            return Samples(m).Any(p => PolygonUtils.Contains(s.BoundaryMm, p[0], p[1]) && InStorey(s, p[2], p[2]));
        }

        /// <summary>元件在管道間內的代表點（立管取中心；水平管取位於管道間內那一段的中點）。</summary>
        public static Point2D Representative(ShaftInfo s, MepInfo m)
        {
            if (m.IsVertical) return new Point2D(m.X1, m.Y1);
            var inside = Samples(m).Where(p => PolygonUtils.Contains(s.BoundaryMm, p[0], p[1])).ToList();
            if (inside.Count == 0) return new Point2D((m.X1 + m.X2) / 2, (m.Y1 + m.Y2) / 2);
            var mid = inside[inside.Count / 2];
            return new Point2D(mid[0], mid[1]);
        }

        /// <summary>立管的平面外形：矩形元件用外框，圓形元件回傳 null（以圓心 + 半徑計算）。</summary>
        public static List<Point2D> RiserFootprint(MepInfo m)
        {
            return m.IsRound || m.FootprintMm == null || m.FootprintMm.Count < 3 ? null : m.FootprintMm;
        }

        /// <summary>
        /// 兩支立管的淨距：中心距扣除兩管半徑與保溫層。矩形斷面以外框計算。重疊時回傳負值或 0。
        /// </summary>
        public static double RiserClearance(MepInfo a, MepInfo b)
        {
            List<Point2D> fa = RiserFootprint(a), fb = RiserFootprint(b);
            var ca = new Point2D(a.X1, a.Y1);
            var cb = new Point2D(b.X1, b.Y1);

            if (fa == null && fb == null)
            {
                double d = Math.Sqrt((ca.X - cb.X) * (ca.X - cb.X) + (ca.Y - cb.Y) * (ca.Y - cb.Y));
                return d - a.OuterRadiusMm - b.OuterRadiusMm;
            }
            if (fa == null) return PolygonUtils.PointPolygonDistance(fb, ca) - a.OuterRadiusMm - b.InsulationMm;
            if (fb == null) return PolygonUtils.PointPolygonDistance(fa, cb) - b.OuterRadiusMm - a.InsulationMm;
            return PolygonUtils.PolygonDistance(fa, fb) - a.InsulationMm - b.InsulationMm;
        }

        /// <summary>立管平面外形（含保溫）與多邊形是否重疊。</summary>
        public static bool RiserTouches(MepInfo m, IList<Point2D> poly, double tolerance = 0)
        {
            var fp = RiserFootprint(m);
            if (fp == null)
                return PolygonUtils.PointPolygonDistance(poly, new Point2D(m.X1, m.Y1)) < m.OuterRadiusMm - tolerance;
            return PolygonUtils.PolygonDistance(fp, poly) < m.InsulationMm - tolerance ||
                   PolygonUtils.PolygonsIntersect(fp, poly);
        }

        /// <summary>線段（水平管中心線）到多邊形的平面距離；穿過或在內部為 0。</summary>
        public static double SegmentPolygonDistance(Point2D a, Point2D b, IList<Point2D> poly)
        {
            if (PolygonUtils.Contains(poly, a.X, a.Y) || PolygonUtils.Contains(poly, b.X, b.Y)) return 0;
            double best = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
                best = Math.Min(best, PolygonUtils.SegmentSegmentDistance(a, b, poly[i], poly[(i + 1) % poly.Count]));
            return best;
        }

        /// <summary>管道間內的隔牆：中心線有一段落在管道間邊界內、且標高與本層重疊的牆。</summary>
        public static List<InnerWallInfo> PartitionWalls(ShaftInfo s)
        {
            var list = new List<InnerWallInfo>();
            foreach (InnerWallInfo w in s.InnerWalls)
            {
                if (!InStorey(s, w.BottomZ, w.TopZ, 50)) continue;
                bool inside = false;
                for (int i = 1; i < 20 && !inside; i++)
                {
                    double t = i / 20.0;
                    inside = PolygonUtils.Contains(s.BoundaryMm, w.X1 + t * (w.X2 - w.X1), w.Y1 + t * (w.Y2 - w.Y1));
                }
                if (inside) list.Add(w);
            }
            return list;
        }

        /// <summary>
        /// 門後方（管道間內側）的維修作業區：門寬 × 指定深度，從牆的管道間側表面算起。
        /// </summary>
        public static List<Point2D> DoorBackZone(DoorInfo d, double depthMm)
        {
            double w = d.WidthMm > 0 ? d.WidthMm : 800;
            double t0 = d.WallThicknessMm / 2;
            double t1 = t0 + depthMm;
            Func<double, double, Point2D> at = (u, v) =>
                new Point2D(d.CenterX + u * d.AlongX + v * d.IntoX, d.CenterY + u * d.AlongY + v * d.IntoY);
            return PolygonUtils.EnsureCcw(new List<Point2D> { at(-w / 2, t0), at(w / 2, t0), at(w / 2, t1), at(-w / 2, t1) });
        }

        /// <summary>門洞的平面範圍（門寬 × 牆厚，兩側各外加 50 mm），用來找門頂上方的梁。</summary>
        public static List<Point2D> DoorOpening(DoorInfo d)
        {
            double w = d.WidthMm > 0 ? d.WidthMm : 800;
            double h = d.WallThicknessMm / 2 + 50;
            Func<double, double, Point2D> at = (u, v) =>
                new Point2D(d.CenterX + u * d.AlongX + v * d.IntoX, d.CenterY + u * d.AlongY + v * d.IntoY);
            return PolygonUtils.EnsureCcw(new List<Point2D> { at(-w / 2, -h), at(w / 2, -h), at(w / 2, h), at(-w / 2, h) });
        }

        /// <summary>元件物件 → 元件參照（Id + 所在模型）。門、牆屬於管道間所在的模型。</summary>
        public static List<ElementRef> Refs(ShaftInfo shaft, params object[] items)
        {
            var list = new List<ElementRef>();
            string own = shaft != null ? shaft.SourceName : null;
            foreach (object o in items ?? new object[0])
            {
                var m = o as MepInfo;
                if (m != null) { list.Add(new ElementRef(m.Id, m.Source)); continue; }
                var b = o as BeamInfo;
                if (b != null) { list.Add(new ElementRef(b.Id, b.Source)); continue; }
                var c = o as ColumnInfo;
                if (c != null) { list.Add(new ElementRef(c.Id, c.Source)); continue; }
                var a = o as AccessoryInfo;
                if (a != null) { list.Add(new ElementRef(a.Id, a.Source)); continue; }
                var w = o as InnerWallInfo;
                if (w != null) { list.Add(new ElementRef(w.Id, w.Source)); continue; }
                var d = o as DoorInfo;
                if (d != null) { list.Add(new ElementRef(d.Id, own)); continue; }
                var wi = o as WallInfo;
                if (wi != null) { list.Add(new ElementRef(wi.Id, own)); continue; }
            }
            return list.GroupBy(r => r.Id + "|" + r.Source).Select(g => g.First()).ToList();
        }

        /// <summary>只回傳主模型元件的 Id（連結模型中的元件無法上色或選取）。</summary>
        public static long[] HostIds(params object[] items)
        {
            var ids = new List<long>();
            foreach (object o in items)
            {
                var m = o as MepInfo;
                if (m != null && string.IsNullOrEmpty(m.Source)) { ids.Add(m.Id); continue; }
                var b = o as BeamInfo;
                if (b != null && string.IsNullOrEmpty(b.Source)) { ids.Add(b.Id); continue; }
                var c = o as ColumnInfo;
                if (c != null && string.IsNullOrEmpty(c.Source)) { ids.Add(c.Id); continue; }
                var a = o as AccessoryInfo;
                if (a != null && string.IsNullOrEmpty(a.Source)) { ids.Add(a.Id); continue; }
                var w = o as InnerWallInfo;
                if (w != null && string.IsNullOrEmpty(w.Source)) { ids.Add(w.Id); continue; }
                var d = o as DoorInfo;
                if (d != null) ids.Add(d.Id);
            }
            return ids.Distinct().ToArray();
        }
    }
}
