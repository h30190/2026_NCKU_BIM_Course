using System;
using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Analysis
{
    /// <summary>一個建議的平移：方向（單位向量）、距離與文字說明。</summary>
    public class MoveOption
    {
        public double Dx, Dy;
        public double DistanceMm;
        public string Direction;

        public string Text
        {
            get { return Direction + " " + DistanceMm.ToString("N0") + " mm"; }
        }
    }

    /// <summary>侵入管道間的一根梁或柱。</summary>
    public class MemberOverlap
    {
        public bool IsBeam;
        public long Id;
        public string Source;
        public string Label;
        public List<Point2D> Footprint;
        /// <summary>與管道間重疊的部分（2D 交集）。</summary>
        public List<Point2D> Overlap = new List<Point2D>();
        public double OverlapAreaM2;
        /// <summary>侵入深度：梁為垂直梁軸方向的重疊寬度；柱為兩方向重疊寬度的較小者。</summary>
        public double OverlapDepthMm;
        public double BottomZ;
        public double TopZ;
        /// <summary>梁底到本層樓板的淨高；柱或不知道樓板標高時為 null。</summary>
        public double? ClearHeightMm;
        /// <summary>侵入深度超過容許值，判定為阻擋。</summary>
        public bool Blocking;
        public object Member;
    }

    /// <summary>穿過梁或柱的立管。</summary>
    public class RiserConflict
    {
        public MepInfo Mep;
        public object Member;
        public string MemberLabel;
        public long MemberId;
        public string MemberSource;
        /// <summary>重疊的標高範圍。</summary>
        public double Z0, Z1;
        /// <summary>立管在管道間內可移動的最小距離；沒有可行位置為 null。</summary>
        public MoveOption Move;
    }

    /// <summary>管道間與結構柱梁的 2D 分析結果（也提供給儀表板畫平面示意圖）。</summary>
    public class StructureReport
    {
        public RectFrame Frame;
        public double ShaftAreaM2;
        public double BlockedAreaM2;
        public double FreeAreaM2;
        /// <summary>扣除梁柱禁區後，管道間內最大的矩形可用空間（短邊、長邊）。</summary>
        public double FreeWidthMm;
        public double FreeDepthMm;
        public List<MemberOverlap> Members = new List<MemberOverlap>();
        public List<RiserConflict> Risers = new List<RiserConflict>();
        /// <summary>管道間整體移動的建議（依距離由小到大）。</summary>
        public List<MoveOption> ShaftMoves = new List<MoveOption>();
        /// <summary>本層、管道間附近所有梁柱的平面投影（禁區）。</summary>
        public List<List<Point2D>> Forbidden = new List<List<Point2D>>();
        /// <summary>本層、管道間附近的梁（畫圖用，含未侵入者）。</summary>
        public List<BeamInfo> NearbyBeams = new List<BeamInfo>();
        public List<ColumnInfo> NearbyColumns = new List<ColumnInfo>();

        public List<MemberOverlap> Blocking { get { return Members.Where(m => m.Blocking).ToList(); } }
    }

    /// <summary>
    /// 管道間避開結構柱梁的分析：
    /// 1. 梁、柱的平面投影當作禁區，與管道間邊界做 2D 交集／差集，得到遮擋面積與剩餘可用淨尺寸；
    /// 2. 立管穿過梁柱（建築平面看不到梁，管線常被畫進梁裡）；
    /// 3. 有侵入時，求管道間或立管可以移動的方向與最小距離。
    /// </summary>
    public static class StructureAnalysis
    {
        public static StructureReport Analyze(ShaftInfo s, RuleConfig cfg)
        {
            var rep = new StructureReport();
            if (s.BoundaryMm.Count < 3) return rep;

            List<Point2D> shaft = PolygonUtils.EnsureCcw(s.BoundaryMm);
            rep.Frame = PolygonUtils.MinAreaRectFrame(shaft);
            rep.ShaftAreaM2 = PolygonUtils.Area(shaft) / 1e6;
            double tol = Math.Max(0, cfg.StructOverlapTolMm);

            rep.NearbyBeams = s.Beams.Where(b => ShaftSpace.BeamInStorey(s, b) && b.WidthMm > 0).ToList();
            rep.NearbyColumns = s.Columns.Where(c => ShaftSpace.ColumnInStorey(s, c) && c.FootprintMm.Count >= 3).ToList();

            foreach (BeamInfo b in rep.NearbyBeams)
            {
                var fp = b.Footprint();
                rep.Forbidden.Add(fp);
                var m = Overlap(shaft, fp, true, b.Id, b.Source, b.Label, b.BottomZ, b.TopZ, b);
                if (m == null) continue;
                // 侵入深度：垂直梁軸方向的重疊寬度
                double len = b.LengthMm;
                if (len > 1e-6)
                {
                    double nx = -(b.Y2 - b.Y1) / len, ny = (b.X2 - b.X1) / len;
                    double lo, hi;
                    PolygonUtils.Project(m.Overlap, nx, ny, out lo, out hi);
                    m.OverlapDepthMm = hi - lo;
                }
                if (ShaftSpace.HasStorey(s)) m.ClearHeightMm = b.BottomZ - s.FloorZ;
                m.Blocking = m.OverlapDepthMm > tol && m.OverlapAreaM2 > 1e-4;
                rep.Members.Add(m);
            }

            foreach (ColumnInfo c in rep.NearbyColumns)
            {
                var fp = PolygonUtils.ConvexHull(c.FootprintMm);
                if (fp.Count < 3) continue;
                rep.Forbidden.Add(fp);
                var m = Overlap(shaft, fp, false, c.Id, c.Source, c.Label, c.BottomZ, c.TopZ, c);
                if (m == null) continue;
                double lo, hi, lo2, hi2;
                PolygonUtils.Project(m.Overlap, rep.Frame.Ux, rep.Frame.Uy, out lo, out hi);
                PolygonUtils.Project(m.Overlap, rep.Frame.Vx, rep.Frame.Vy, out lo2, out hi2);
                m.OverlapDepthMm = Math.Min(hi - lo, hi2 - lo2);
                m.Blocking = m.OverlapDepthMm > tol && m.OverlapAreaM2 > 1e-4;
                rep.Members.Add(m);
            }

            FreeSpace(shaft, rep);

            if (rep.Blocking.Count > 0)
                rep.ShaftMoves = SuggestShaftMoves(shaft, rep, cfg);

            FindRiserConflicts(s, shaft, rep, cfg);
            return rep;
        }

        private static MemberOverlap Overlap(List<Point2D> shaft, List<Point2D> fp, bool isBeam, long id, string source,
            string label, double z0, double z1, object member)
        {
            var clip = PolygonUtils.ClipByConvex(shaft, fp);
            double area = PolygonUtils.Area(clip) / 1e6;
            if (clip.Count < 3 || area <= 0) return null;
            return new MemberOverlap
            {
                IsBeam = isBeam, Id = id, Source = source, Label = label, Footprint = fp,
                Overlap = clip, OverlapAreaM2 = area, BottomZ = z0, TopZ = z1, Member = member
            };
        }

        // ------------------------------------------------------------------ 可用淨空間（2D 差集）

        /// <summary>
        /// 以管道間自己的座標系做網格取樣：在管道間內、且不在任何阻擋梁柱投影內的格子為可用。
        /// 得到遮擋面積、可用面積，以及最大的可用矩形（直方圖法）。
        /// </summary>
        private static void FreeSpace(List<Point2D> shaft, StructureReport rep)
        {
            RectFrame f = rep.Frame;
            if (f.HalfU <= 0 || f.HalfV <= 0)
            {
                rep.FreeAreaM2 = rep.ShaftAreaM2;
                return;
            }

            double step = Math.Max(10, Math.Min(f.HalfU, f.HalfV) * 2 / 60);
            int nu = Math.Max(1, (int)Math.Ceiling(2 * f.HalfU / step));
            int nv = Math.Max(1, (int)Math.Ceiling(2 * f.HalfV / step));
            var blocking = rep.Blocking.Select(m => m.Footprint).ToList();

            var free = new bool[nv, nu];
            int inside = 0, blocked = 0;
            for (int j = 0; j < nv; j++)
            {
                for (int i = 0; i < nu; i++)
                {
                    double u = -f.HalfU + (i + 0.5) * step;
                    double v = -f.HalfV + (j + 0.5) * step;
                    Point2D p = f.ToWorld(u, v);
                    if (!PolygonUtils.Contains(shaft, p.X, p.Y)) continue;
                    inside++;
                    bool b = blocking.Any(fp => PolygonUtils.Contains(fp, p.X, p.Y));
                    if (b) blocked++;
                    else free[j, i] = true;
                }
            }

            if (inside == 0)
            {
                rep.FreeAreaM2 = rep.ShaftAreaM2;
                return;
            }
            rep.BlockedAreaM2 = rep.ShaftAreaM2 * blocked / inside;
            rep.FreeAreaM2 = rep.ShaftAreaM2 - rep.BlockedAreaM2;

            // 最大矩形（每列累計高度 + 單調堆疊）
            var heights = new int[nu];
            double bestArea = 0, bestW = 0, bestH = 0;
            for (int j = 0; j < nv; j++)
            {
                for (int i = 0; i < nu; i++) heights[i] = free[j, i] ? heights[i] + 1 : 0;
                var stack = new Stack<int>();
                for (int i = 0; i <= nu; i++)
                {
                    int h = i == nu ? 0 : heights[i];
                    while (stack.Count > 0 && heights[stack.Peek()] >= h)
                    {
                        int top = stack.Pop();
                        int height = heights[top];
                        int width = stack.Count == 0 ? i : i - stack.Peek() - 1;
                        double a = height * width;
                        if (a > bestArea)
                        {
                            bestArea = a;
                            bestW = width * step;
                            bestH = height * step;
                        }
                    }
                    if (i < nu) stack.Push(i);
                }
            }
            rep.FreeWidthMm = Math.Min(bestW, bestH);
            rep.FreeDepthMm = Math.Max(bestW, bestH);
        }

        // ------------------------------------------------------------------ 管道間移動建議

        /// <summary>
        /// 沿候選方向（管道間長、短邊的正負方向，以及每根阻擋梁的法線方向）平移管道間，
        /// 找出完全避開所有梁柱禁區（侵入不超過容許值）的最小距離。
        /// </summary>
        public static List<MoveOption> SuggestShaftMoves(List<Point2D> shaft, StructureReport rep, RuleConfig cfg)
        {
            var dirs = new List<double[]>();
            AddDir(dirs, rep.Frame.Ux, rep.Frame.Uy);
            AddDir(dirs, -rep.Frame.Ux, -rep.Frame.Uy);
            AddDir(dirs, rep.Frame.Vx, rep.Frame.Vy);
            AddDir(dirs, -rep.Frame.Vx, -rep.Frame.Vy);
            foreach (MemberOverlap m in rep.Blocking)
            {
                var b = m.Member as BeamInfo;
                if (b == null || b.LengthMm < 1e-6) continue;
                double nx = -(b.Y2 - b.Y1) / b.LengthMm, ny = (b.X2 - b.X1) / b.LengthMm;
                AddDir(dirs, nx, ny);
                AddDir(dirs, -nx, -ny);
            }

            double tol = Math.Max(0, cfg.StructOverlapTolMm);
            double limit = cfg.MaxMoveSearchMm > 0 ? cfg.MaxMoveSearchMm : 3000;
            var result = new List<MoveOption>();

            foreach (double[] d in dirs)
            {
                double t = 0;
                bool ok = false;
                for (int iter = 0; iter < 12 && t <= limit; iter++)
                {
                    var moved = PolygonUtils.Translate(shaft, d[0] * t, d[1] * t);
                    double need = t;
                    bool hit = false;
                    foreach (var fp in rep.Forbidden)
                    {
                        if (Penetration(moved, fp, d[0], d[1]) <= tol + 0.5) continue;
                        hit = true;
                        double fMin, fMax, sMin, sMax;
                        PolygonUtils.Project(fp, d[0], d[1], out fMin, out fMax);
                        PolygonUtils.Project(shaft, d[0], d[1], out sMin, out sMax);
                        need = Math.Max(need, fMax - sMin - tol);
                    }
                    if (!hit) { ok = true; break; }
                    t = need > t + 0.5 ? need : t + 10;
                }
                if (!ok || t <= 0 || t > limit) continue;
                double dist = Math.Ceiling(t / 10.0) * 10;
                result.Add(new MoveOption { Dx = d[0], Dy = d[1], DistanceMm = dist, Direction = DirectionText(d[0], d[1]) });
            }

            return result.OrderBy(m => m.DistanceMm)
                .GroupBy(m => m.Direction).Select(g => g.First())
                .Take(3).ToList();
        }

        /// <summary>兩多邊形在方向 d 上的重疊深度（以交集在 d 方向的寬度近似）；不重疊為 0。</summary>
        private static double Penetration(List<Point2D> moved, List<Point2D> convexFp, double dx, double dy)
        {
            var clip = PolygonUtils.ClipByConvex(moved, convexFp);
            if (clip.Count < 3 || PolygonUtils.Area(clip) < 1) return 0;
            double lo, hi, lo2, hi2;
            PolygonUtils.Project(clip, dx, dy, out lo, out hi);
            PolygonUtils.Project(clip, -dy, dx, out lo2, out hi2);
            return Math.Min(hi - lo, hi2 - lo2);
        }

        private static void AddDir(List<double[]> dirs, double x, double y)
        {
            double len = Math.Sqrt(x * x + y * y);
            if (len < 1e-9) return;
            x /= len; y /= len;
            foreach (var d in dirs)
                if (d[0] * x + d[1] * y > 0.996) return; // 約 5° 以內視為同方向
            dirs.Add(new[] { x, y });
        }

        /// <summary>平面圖方向（+X 往右、+Y 往上）的文字，例如「往右（+X）」「往左上（135°）」。</summary>
        public static string DirectionText(double dx, double dy)
        {
            double deg = Math.Atan2(dy, dx) * 180 / Math.PI;
            if (deg < 0) deg += 360;
            string[] names = { "往右", "往右上", "往上", "往左上", "往左", "往左下", "往下", "往右下" };
            int idx = (int)Math.Round(deg / 45.0) % 8;
            double snapped = idx * 45.0;
            bool axis = Math.Abs(deg - snapped) < 2 || Math.Abs(deg - snapped - 360) < 2;
            if (axis)
            {
                switch (idx)
                {
                    case 0: return "往右（+X）";
                    case 2: return "往上（+Y）";
                    case 4: return "往左（−X）";
                    case 6: return "往下（−Y）";
                }
            }
            return names[idx] + "（" + deg.ToString("F0") + "°）";
        }

        // ------------------------------------------------------------------ 立管與梁柱

        private static void FindRiserConflicts(ShaftInfo s, List<Point2D> shaft, StructureReport rep, RuleConfig cfg)
        {
            // 管道間內或邊界附近（300 mm）的立管
            var risers = s.Mep.Where(m => m.IsVertical &&
                (PolygonUtils.Contains(shaft, m.X1, m.Y1) ||
                 PolygonUtils.DistanceToBoundary(shaft, new Point2D(m.X1, m.Y1)) < 300) &&
                ShaftSpace.InStorey(s, m.MinZ, m.MaxZ)).ToList();

            foreach (MepInfo m in risers)
            {
                foreach (BeamInfo b in rep.NearbyBeams)
                {
                    double z0 = Math.Max(m.MinZ, b.BottomZ), z1 = Math.Min(m.MaxZ, b.TopZ);
                    if (z1 - z0 < 10) continue;
                    var fp = b.Footprint();
                    if (!ShaftSpace.RiserTouches(m, fp, 1)) continue;
                    rep.Risers.Add(new RiserConflict
                    {
                        Mep = m, Member = b, MemberLabel = b.Label, MemberId = b.Id, MemberSource = b.Source,
                        Z0 = z0, Z1 = z1, Move = SuggestRiserMove(m, fp, shaft, rep, cfg, b)
                    });
                }
                foreach (ColumnInfo c in rep.NearbyColumns)
                {
                    double z0 = Math.Max(m.MinZ, c.BottomZ), z1 = Math.Min(m.MaxZ, c.TopZ);
                    if (z1 - z0 < 10) continue;
                    var fp = PolygonUtils.ConvexHull(c.FootprintMm);
                    if (fp.Count < 3 || !ShaftSpace.RiserTouches(m, fp, 1)) continue;
                    rep.Risers.Add(new RiserConflict
                    {
                        Mep = m, Member = c, MemberLabel = c.Label, MemberId = c.Id, MemberSource = c.Source,
                        Z0 = z0, Z1 = z1, Move = SuggestRiserMove(m, fp, shaft, rep, cfg, null)
                    });
                }
            }
        }

        /// <summary>
        /// 立管移出梁柱投影的最小距離：新位置須在管道間內（離邊界至少一個半徑），
        /// 且不碰到任何梁柱禁區，與禁區保留 minPipeClearMm。
        /// </summary>
        public static MoveOption SuggestRiserMove(MepInfo m, List<Point2D> hitFp, List<Point2D> shaft,
            StructureReport rep, RuleConfig cfg, BeamInfo beam)
        {
            var dirs = new List<double[]>();
            if (beam != null && beam.LengthMm > 1e-6)
            {
                double nx = -(beam.Y2 - beam.Y1) / beam.LengthMm, ny = (beam.X2 - beam.X1) / beam.LengthMm;
                AddDir(dirs, nx, ny);
                AddDir(dirs, -nx, -ny);
            }
            AddDir(dirs, rep.Frame.Ux, rep.Frame.Uy);
            AddDir(dirs, -rep.Frame.Ux, -rep.Frame.Uy);
            AddDir(dirs, rep.Frame.Vx, rep.Frame.Vy);
            AddDir(dirs, -rep.Frame.Vx, -rep.Frame.Vy);

            double r = m.OuterRadiusMm;
            double clr = Math.Max(0, cfg.MinPipeClearMm);
            MoveOption best = null;

            foreach (double[] d in dirs)
            {
                double t = 0;
                for (int iter = 0; iter < 10; iter++)
                {
                    var c = new Point2D(m.X1 + d[0] * t, m.Y1 + d[1] * t);
                    double need = t;
                    bool hit = false;
                    foreach (var fp in rep.Forbidden)
                    {
                        if (PolygonUtils.PointPolygonDistance(fp, c) >= r + clr - 0.5) continue;
                        hit = true;
                        double fMin, fMax;
                        PolygonUtils.Project(fp, d[0], d[1], out fMin, out fMax);
                        double c0 = m.X1 * d[0] + m.Y1 * d[1];
                        need = Math.Max(need, fMax + r + clr - c0);
                    }
                    if (!hit)
                    {
                        bool inShaft = PolygonUtils.Contains(shaft, c.X, c.Y) &&
                                       PolygonUtils.DistanceToBoundary(shaft, c) >= r - 0.5;
                        if (inShaft && t > 0)
                        {
                            double dist = Math.Ceiling(t / 10.0) * 10;
                            if (best == null || dist < best.DistanceMm)
                                best = new MoveOption { Dx = d[0], Dy = d[1], DistanceMm = dist, Direction = DirectionText(d[0], d[1]) };
                        }
                        break;
                    }
                    t = need > t + 0.5 ? need : t + 10;
                }
            }
            return best;
        }
    }
}
