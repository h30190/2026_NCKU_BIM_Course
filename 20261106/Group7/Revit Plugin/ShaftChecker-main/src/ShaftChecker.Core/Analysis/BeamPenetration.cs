using System;
using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Analysis
{
    /// <summary>水平管線與梁的一處衝突（穿梁、局部撞梁或沿梁內部配置）。</summary>
    public class BeamHit
    {
        public MepInfo Mep;
        public BeamInfo Beam;
        /// <summary>穿越點的平面位置。</summary>
        public double X, Y;
        /// <summary>穿越點管中心標高。</summary>
        public double CenterZ;
        /// <summary>孔徑（管外徑或風管高度，含保溫）。</summary>
        public double HoleMm;
        /// <summary>距梁起點（X1, Y1）的距離，判斷孔距用。</summary>
        public double AlongMm;
        /// <summary>距支承面（柱面或梁端）的距離。</summary>
        public double SupportDistMm;
        /// <summary>完整穿越梁腹（管頂、管底都在梁內）。</summary>
        public bool Through;
        /// <summary>沿著梁走（夾角小於 30°），管線位於梁體內。</summary>
        public bool Parallel;
        public List<string> Problems = new List<string>();
        public string Suggestion;

        public bool Ok { get { return Problems.Count == 0; } }
    }

    /// <summary>
    /// 管線穿梁檢討。台灣建築技術規則沒有穿梁的明文規定，以下採結構實務常用的限制（門檻可在設定檔調整，
    /// 實際以結構技師的要求為準）：
    /// 孔徑 ≤ 梁深 1/3；孔中心位於梁深中央 1/3；距支承面（柱面）≥ 2 倍梁深；相鄰孔中心距 ≥ 3 倍平均孔徑。
    /// </summary>
    public static class BeamPenetration
    {
        public static List<BeamHit> Analyze(ShaftInfo s, RuleConfig cfg)
        {
            var hits = new List<BeamHit>();
            foreach (MepInfo m in s.Mep)
            {
                if (m.IsVertical) continue; // 立管穿梁屬 S1
                foreach (BeamInfo b in s.Beams)
                {
                    if (b.LengthMm < 1 || b.DepthMm <= 0) continue;
                    BeamHit h = Test(m, b, s, cfg);
                    if (h != null) hits.Add(h);
                }
            }

            // 同一根梁上相鄰孔的間距
            foreach (var g in hits.Where(h => h.Through && !h.Parallel).GroupBy(h => h.Beam.Id.ToString() + "|" + h.Beam.Source))
            {
                var list = g.OrderBy(h => h.AlongMm).ToList();
                for (int i = 1; i < list.Count; i++)
                {
                    BeamHit a = list[i - 1], c = list[i];
                    double need = cfg.BeamHoleSpacingFactor * (a.HoleMm + c.HoleMm) / 2;
                    double gap = c.AlongMm - a.AlongMm;
                    if (gap < need)
                        c.Problems.Add("與相鄰孔（" + a.Mep.Label + "）中心距 " + gap.ToString("F0") + " mm，小於 " +
                                       cfg.BeamHoleSpacingFactor.ToString("0.#") + " 倍平均孔徑 " + need.ToString("F0") + " mm");
                }
            }

            return hits.Where(h => !h.Ok).ToList();
        }

        private static BeamHit Test(MepInfo m, BeamInfo b, ShaftInfo s, RuleConfig cfg)
        {
            var p1 = new Point2D(m.X1, m.Y1);
            var p2 = new Point2D(m.X2, m.Y2);
            var a1 = new Point2D(b.X1, b.Y1);
            var a2 = new Point2D(b.X2, b.Y2);
            double plen = Math.Sqrt((m.X2 - m.X1) * (m.X2 - m.X1) + (m.Y2 - m.Y1) * (m.Y2 - m.Y1));
            if (plen < 1) return null;

            double bl = b.LengthMm;
            double ux = (b.X2 - b.X1) / bl, uy = (b.Y2 - b.Y1) / bl;
            double px = (m.X2 - m.X1) / plen, py = (m.Y2 - m.Y1) / plen;
            double sin = Math.Abs(ux * py - uy * px);
            double rv = m.VerticalSizeMm / 2;
            double rh = m.OuterRadiusMm;

            if (sin >= 0.5)
            {
                double t, u;
                if (!PolygonUtils.LineIntersection(p1, p2, a1, a2, out t, out u)) return null;
                double tolT = (b.WidthMm / 2 + rh) / plen;
                if (t < -tolT || t > 1 + tolT || u < 0 || u > 1) return null;
                double tc = Math.Max(0, Math.Min(1, t));
                double zc = m.Z1 + tc * (m.Z2 - m.Z1);
                double top = zc + rv, bottom = zc - rv;
                if (top <= b.BottomZ + 1 || bottom >= b.TopZ - 1) return null; // 在梁下或梁上通過

                var h = new BeamHit
                {
                    Mep = m, Beam = b, X = b.X1 + u * (b.X2 - b.X1), Y = b.Y1 + u * (b.Y2 - b.Y1),
                    CenterZ = zc, HoleMm = m.VerticalSizeMm, AlongMm = u * bl
                };
                h.SupportDistMm = SupportDistance(b, h.AlongMm, s);
                double depth = b.DepthMm;
                double mid = (b.BottomZ + b.TopZ) / 2;
                double down = top - b.BottomZ + cfg.PipeBeamClearMm;

                if (bottom < b.BottomZ || top > b.TopZ)
                {
                    h.Through = false;
                    h.Problems.Add("管線局部撞梁：管" + (top > b.TopZ ? "頂高出梁頂 " + (top - b.TopZ).ToString("F0") : "底低於梁底 " + (b.BottomZ - bottom).ToString("F0")) +
                                   " mm（建築平面看不到梁，管線標高可能錯誤）");
                    h.Suggestion = "建議改走梁下：管線下移至少 " + down.ToString("N0") + " mm（管頂低於梁底 " +
                                   cfg.PipeBeamClearMm.ToString("F0") + " mm）";
                    if (m.VerticalSizeMm <= depth * cfg.BeamHoleMaxRatio)
                        h.Suggestion += "；或調整標高 " + Math.Abs(zc - mid).ToString("N0") + " mm 使管中心位於梁深中央後預留穿梁套管（需結構技師同意）";
                    return h;
                }

                h.Through = true;
                double maxHole = depth * cfg.BeamHoleMaxRatio;
                if (h.HoleMm > maxHole)
                    h.Problems.Add("孔徑 " + h.HoleMm.ToString("F0") + " mm 超過梁深 " + depth.ToString("F0") + " mm 的 " +
                                   Fraction(cfg.BeamHoleMaxRatio) + "（" + maxHole.ToString("F0") + " mm）");

                double band = depth * cfg.BeamHoleCenterBandRatio / 2;
                if (Math.Abs(zc - mid) > band)
                    h.Problems.Add("孔中心偏離梁深中央 " + Math.Abs(zc - mid).ToString("F0") + " mm，未在中央 " +
                                   Fraction(cfg.BeamHoleCenterBandRatio) + " 範圍（±" + band.ToString("F0") + " mm）內");

                double needEnd = depth * cfg.BeamHoleEndDistFactor;
                if (h.SupportDistMm < needEnd)
                    h.Problems.Add("距支承面 " + h.SupportDistMm.ToString("F0") + " mm，小於 " +
                                   cfg.BeamHoleEndDistFactor.ToString("0.#") + " 倍梁深（" + needEnd.ToString("F0") + " mm，梁端塑鉸區）");

                if (h.Problems.Count > 0)
                {
                    var tips = new List<string>();
                    if (h.SupportDistMm < needEnd)
                        tips.Add("沿梁方向移離支承面至少 " + Math.Ceiling(needEnd - h.SupportDistMm).ToString("N0") + " mm");
                    if (Math.Abs(zc - mid) > band)
                        tips.Add((zc > mid ? "下移 " : "上移 ") + Math.Ceiling(Math.Abs(zc - mid) - band).ToString("N0") + " mm 進入梁深中央");
                    if (h.HoleMm > maxHole)
                        tips.Add("孔徑過大無法穿梁，改走梁下（下移至少 " + down.ToString("N0") + " mm）或分成多支小管");
                    h.Suggestion = string.Join("；", tips);
                }
                return h;
            }
            else
            {
                // 幾乎平行：檢查管線是否有一段落在梁體內
                int n = 24, inBeam = 0;
                for (int i = 0; i <= n; i++)
                {
                    double t = (double)i / n;
                    double x = m.X1 + t * (m.X2 - m.X1), y = m.Y1 + t * (m.Y2 - m.Y1), z = m.Z1 + t * (m.Z2 - m.Z1);
                    double along = (x - b.X1) * ux + (y - b.Y1) * uy;
                    double lat = Math.Abs(-(x - b.X1) * uy + (y - b.Y1) * ux);
                    if (along < 0 || along > bl) continue;
                    if (lat > b.WidthMm / 2 + rh) continue;
                    if (z + rv <= b.BottomZ + 1 || z - rv >= b.TopZ - 1) continue;
                    inBeam++;
                }
                double inLen = plen * inBeam / (n + 1);
                if (inBeam < 2 || inLen < Math.Max(b.WidthMm, 200)) return null;

                double zc = (m.Z1 + m.Z2) / 2;
                double down = zc + rv - b.BottomZ + cfg.PipeBeamClearMm;
                var h = new BeamHit
                {
                    Mep = m, Beam = b, X = (m.X1 + m.X2) / 2, Y = (m.Y1 + m.Y2) / 2,
                    CenterZ = zc, HoleMm = m.VerticalSizeMm, Parallel = true
                };
                h.Problems.Add("管線沿梁配置，約 " + inLen.ToString("N0") + " mm 長度位於梁體內（建築平面看不到梁，管線標高可能錯誤）");
                h.Suggestion = "建議下移至少 " + down.ToString("N0") + " mm 改走梁下，或水平移出梁寬範圍";
                return h;
            }
        }

        /// <summary>
        /// 穿越點到支承面的距離：梁兩端若有柱，量到柱面（柱投影在梁軸上的範圍）；沒有柱則量到梁端。
        /// </summary>
        public static double SupportDistance(BeamInfo b, double along, ShaftInfo s)
        {
            double bl = b.LengthMm;
            double ux = (b.X2 - b.X1) / bl, uy = (b.Y2 - b.Y1) / bl;
            double face0 = 0, face1 = bl;

            foreach (ColumnInfo c in s.Columns)
            {
                if (c.FootprintMm.Count < 3) continue;
                if (c.TopZ < b.BottomZ - 10 || c.BottomZ > b.TopZ + 10) continue;
                double lo = double.MaxValue, hi = double.MinValue;
                foreach (var p in c.FootprintMm)
                {
                    double a = (p.X - b.X1) * ux + (p.Y - b.Y1) * uy;
                    lo = Math.Min(lo, a);
                    hi = Math.Max(hi, a);
                }
                bool nearStart = PolygonUtils.PointPolygonDistance(c.FootprintMm, new Point2D(b.X1, b.Y1)) < 300;
                bool nearEnd = PolygonUtils.PointPolygonDistance(c.FootprintMm, new Point2D(b.X2, b.Y2)) < 300;
                if (nearStart) face0 = Math.Max(face0, hi);
                if (nearEnd) face1 = Math.Min(face1, lo);
            }
            return Math.Max(0, Math.Min(along - face0, face1 - along));
        }

        private static string Fraction(double r)
        {
            if (Math.Abs(r - 1.0 / 3.0) < 0.005) return "1/3";
            if (Math.Abs(r - 0.25) < 0.005) return "1/4";
            if (Math.Abs(r - 0.5) < 0.005) return "1/2";
            return (r * 100).ToString("F0") + "%";
        }
    }
}
