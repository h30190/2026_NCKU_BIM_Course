using System;
using System.Collections.Generic;
using System.Linq;

namespace ShaftChecker.Core.Geometry
{
    /// <summary>平面多邊形工具：面積、凸包、最小外接矩形（旋轉卡尺法）。</summary>
    public static class PolygonUtils
    {
        /// <summary>多邊形面積（鞋帶公式），回傳絕對值，單位與輸入相同的平方。</summary>
        public static double Area(IList<Point2D> pts)
        {
            if (pts == null || pts.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Point2D a = pts[i];
                Point2D b = pts[(i + 1) % pts.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            return Math.Abs(sum) / 2.0;
        }

        /// <summary>Andrew monotone chain 凸包，回傳逆時針點序。</summary>
        public static List<Point2D> ConvexHull(IList<Point2D> input)
        {
            var pts = input
                .GroupBy(p => new { X = Math.Round(p.X, 3), Y = Math.Round(p.Y, 3) })
                .Select(g => g.First())
                .OrderBy(p => p.X).ThenBy(p => p.Y)
                .ToList();

            if (pts.Count < 3) return pts;

            var hull = new List<Point2D>();

            // 下半部
            foreach (var p in pts)
            {
                while (hull.Count >= 2 && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            // 上半部
            int lowerCount = hull.Count + 1;
            for (int i = pts.Count - 2; i >= 0; i--)
            {
                var p = pts[i];
                while (hull.Count >= lowerCount && Cross(hull[hull.Count - 2], hull[hull.Count - 1], p) <= 0)
                    hull.RemoveAt(hull.Count - 1);
                hull.Add(p);
            }

            hull.RemoveAt(hull.Count - 1);
            return hull;
        }

        /// <summary>
        /// 最小面積外接矩形。管道間若是矩形，短邊即淨寬、長邊即淨深；
        /// 不規則形狀則取外接矩形作為近似值。
        /// </summary>
        public static RectResult MinAreaRectangle(IList<Point2D> pts)
        {
            var result = new RectResult();
            var hull = ConvexHull(pts);
            if (hull.Count < 3) return result;

            double bestArea = double.MaxValue;

            for (int i = 0; i < hull.Count; i++)
            {
                Point2D a = hull[i];
                Point2D b = hull[(i + 1) % hull.Count];
                double dx = b.X - a.X;
                double dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;

                double ux = dx / len, uy = dy / len;   // 邊方向
                double vx = -uy, vy = ux;              // 法線方向

                double minU = double.MaxValue, maxU = double.MinValue;
                double minV = double.MaxValue, maxV = double.MinValue;
                foreach (var p in hull)
                {
                    double pu = p.X * ux + p.Y * uy;
                    double pv = p.X * vx + p.Y * vy;
                    if (pu < minU) minU = pu;
                    if (pu > maxU) maxU = pu;
                    if (pv < minV) minV = pv;
                    if (pv > maxV) maxV = pv;
                }

                double w = maxU - minU;
                double h = maxV - minV;
                double area = w * h;
                if (area < bestArea)
                {
                    bestArea = area;
                    result.ShortSide = Math.Min(w, h);
                    result.LongSide = Math.Max(w, h);
                }
            }

            return result;
        }

        /// <summary>多邊形面積重心；面積為 0 時回傳頂點平均。</summary>
        public static Point2D Centroid(IList<Point2D> pts)
        {
            if (pts == null || pts.Count == 0) return new Point2D(0, 0);

            double a2 = 0, cx = 0, cy = 0;
            for (int i = 0; i < pts.Count; i++)
            {
                Point2D p = pts[i];
                Point2D q = pts[(i + 1) % pts.Count];
                double cross = p.X * q.Y - q.X * p.Y;
                a2 += cross;
                cx += (p.X + q.X) * cross;
                cy += (p.Y + q.Y) * cross;
            }

            if (Math.Abs(a2) < 1e-9)
                return new Point2D(pts.Average(p => p.X), pts.Average(p => p.Y));

            return new Point2D(cx / (3 * a2), cy / (3 * a2));
        }

        /// <summary>點是否在多邊形內（射線法）。</summary>
        public static bool Contains(IList<Point2D> pts, double x, double y)
        {
            if (pts == null || pts.Count < 3) return false;

            bool inside = false;
            for (int i = 0, j = pts.Count - 1; i < pts.Count; j = i++)
            {
                double xi = pts[i].X, yi = pts[i].Y;
                double xj = pts[j].X, yj = pts[j].Y;
                if ((yi > y) != (yj > y) && x < (xj - xi) * (y - yi) / (yj - yi) + xi)
                    inside = !inside;
            }
            return inside;
        }

        // ------------------------------------------------------------------ 以下供管線與結構檢討使用

        /// <summary>以中心軸線與寬度建立矩形（逆時針）。軸線長度為 0 時回傳以寬度為邊長的正方形。</summary>
        public static List<Point2D> RectangleFromAxis(Point2D a, Point2D b, double width)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len = Math.Sqrt(dx * dx + dy * dy);
            double ux, uy;
            if (len < 1e-9) { ux = 1; uy = 0; a = new Point2D(a.X - width / 2, a.Y); b = new Point2D(a.X + width, a.Y); }
            else { ux = dx / len; uy = dy / len; }
            double nx = -uy * width / 2, ny = ux * width / 2;
            return new List<Point2D>
            {
                new Point2D(a.X - nx, a.Y - ny),
                new Point2D(b.X - nx, b.Y - ny),
                new Point2D(b.X + nx, b.Y + ny),
                new Point2D(a.X + nx, a.Y + ny)
            };
        }

        /// <summary>
        /// 最小外接矩形的座標系：中心、長邊方向 U、短邊方向 V、半長、半寬。
        /// 用來把管道間轉到自己的座標系（例如找可用淨空間、描述移動方向）。
        /// </summary>
        public static RectFrame MinAreaRectFrame(IList<Point2D> pts)
        {
            var frame = new RectFrame { Ux = 1, Uy = 0, Vx = 0, Vy = 1 };
            var hull = ConvexHull(pts);
            if (hull.Count < 3) return frame;

            double bestArea = double.MaxValue;
            for (int i = 0; i < hull.Count; i++)
            {
                Point2D a = hull[i];
                Point2D b = hull[(i + 1) % hull.Count];
                double dx = b.X - a.X, dy = b.Y - a.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) continue;
                double ux = dx / len, uy = dy / len, vx = -uy, vy = ux;

                double minU = double.MaxValue, maxU = double.MinValue, minV = double.MaxValue, maxV = double.MinValue;
                foreach (var p in hull)
                {
                    double pu = p.X * ux + p.Y * uy, pv = p.X * vx + p.Y * vy;
                    if (pu < minU) minU = pu;
                    if (pu > maxU) maxU = pu;
                    if (pv < minV) minV = pv;
                    if (pv > maxV) maxV = pv;
                }
                double area = (maxU - minU) * (maxV - minV);
                if (area < bestArea - 1e-6)
                {
                    bestArea = area;
                    double cu = (minU + maxU) / 2, cv = (minV + maxV) / 2;
                    bool uLong = (maxU - minU) >= (maxV - minV);
                    frame.Cx = cu * ux + cv * vx;
                    frame.Cy = cu * uy + cv * vy;
                    if (uLong)
                    {
                        frame.Ux = ux; frame.Uy = uy; frame.Vx = vx; frame.Vy = vy;
                        frame.HalfU = (maxU - minU) / 2; frame.HalfV = (maxV - minV) / 2;
                    }
                    else
                    {
                        frame.Ux = vx; frame.Uy = vy; frame.Vx = -ux; frame.Vy = -uy;
                        frame.HalfU = (maxV - minV) / 2; frame.HalfV = (maxU - minU) / 2;
                    }
                }
            }
            return frame;
        }

        /// <summary>多邊形改成逆時針點序（複製一份）。</summary>
        public static List<Point2D> EnsureCcw(IList<Point2D> pts)
        {
            var list = new List<Point2D>(pts);
            double sum = 0;
            for (int i = 0; i < list.Count; i++)
            {
                Point2D a = list[i], b = list[(i + 1) % list.Count];
                sum += a.X * b.Y - b.X * a.Y;
            }
            if (sum < 0) list.Reverse();
            return list;
        }

        /// <summary>多邊形投影到單位向量 (ux, uy) 上的最小、最大值。</summary>
        public static void Project(IList<Point2D> pts, double ux, double uy, out double min, out double max)
        {
            min = double.MaxValue;
            max = double.MinValue;
            foreach (var p in pts)
            {
                double d = p.X * ux + p.Y * uy;
                if (d < min) min = d;
                if (d > max) max = d;
            }
        }

        public static List<Point2D> Translate(IList<Point2D> pts, double dx, double dy)
        {
            return pts.Select(p => new Point2D(p.X + dx, p.Y + dy)).ToList();
        }

        /// <summary>兩線段是否相交（含端點接觸）。</summary>
        public static bool SegmentsIntersect(Point2D p1, Point2D p2, Point2D q1, Point2D q2)
        {
            double d1 = Cross(q1, q2, p1), d2 = Cross(q1, q2, p2);
            double d3 = Cross(p1, p2, q1), d4 = Cross(p1, p2, q2);
            if (((d1 > 0 && d2 < 0) || (d1 < 0 && d2 > 0)) && ((d3 > 0 && d4 < 0) || (d3 < 0 && d4 > 0))) return true;
            const double eps = 1e-9;
            if (Math.Abs(d1) < eps && OnSegment(q1, q2, p1)) return true;
            if (Math.Abs(d2) < eps && OnSegment(q1, q2, p2)) return true;
            if (Math.Abs(d3) < eps && OnSegment(p1, p2, q1)) return true;
            if (Math.Abs(d4) < eps && OnSegment(p1, p2, q2)) return true;
            return false;
        }

        /// <summary>
        /// 兩條線段（或延長線）的交點參數：p = p1 + t (p2 − p1)，q = q1 + u (q2 − q1)。平行時回傳 false。
        /// </summary>
        public static bool LineIntersection(Point2D p1, Point2D p2, Point2D q1, Point2D q2, out double t, out double u)
        {
            double rx = p2.X - p1.X, ry = p2.Y - p1.Y, sx = q2.X - q1.X, sy = q2.Y - q1.Y;
            double denom = rx * sy - ry * sx;
            t = u = 0;
            if (Math.Abs(denom) < 1e-12) return false;
            double qpx = q1.X - p1.X, qpy = q1.Y - p1.Y;
            t = (qpx * sy - qpy * sx) / denom;
            u = (qpx * ry - qpy * rx) / denom;
            return true;
        }

        public static double PointSegmentDistance(Point2D p, Point2D a, Point2D b)
        {
            double dx = b.X - a.X, dy = b.Y - a.Y;
            double len2 = dx * dx + dy * dy;
            double t = len2 < 1e-12 ? 0 : ((p.X - a.X) * dx + (p.Y - a.Y) * dy) / len2;
            t = Math.Max(0, Math.Min(1, t));
            double cx = a.X + t * dx - p.X, cy = a.Y + t * dy - p.Y;
            return Math.Sqrt(cx * cx + cy * cy);
        }

        public static double SegmentSegmentDistance(Point2D a1, Point2D a2, Point2D b1, Point2D b2)
        {
            if (SegmentsIntersect(a1, a2, b1, b2)) return 0;
            return Math.Min(Math.Min(PointSegmentDistance(a1, b1, b2), PointSegmentDistance(a2, b1, b2)),
                            Math.Min(PointSegmentDistance(b1, a1, a2), PointSegmentDistance(b2, a1, a2)));
        }

        /// <summary>點到多邊形邊界的最短距離（不分內外）。</summary>
        public static double DistanceToBoundary(IList<Point2D> poly, Point2D p)
        {
            double best = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
                best = Math.Min(best, PointSegmentDistance(p, poly[i], poly[(i + 1) % poly.Count]));
            return best;
        }

        /// <summary>點到多邊形的距離：在內部為 0。</summary>
        public static double PointPolygonDistance(IList<Point2D> poly, Point2D p)
        {
            if (Contains(poly, p.X, p.Y)) return 0;
            return DistanceToBoundary(poly, p);
        }

        /// <summary>兩多邊形是否重疊（邊相交或一方包含另一方）。</summary>
        public static bool PolygonsIntersect(IList<Point2D> a, IList<Point2D> b)
        {
            if (a.Count == 0 || b.Count == 0) return false;
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                    if (SegmentsIntersect(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count])) return true;
            return Contains(a, b[0].X, b[0].Y) || Contains(b, a[0].X, a[0].Y);
        }

        /// <summary>兩多邊形的最短距離：重疊時為 0。</summary>
        public static double PolygonDistance(IList<Point2D> a, IList<Point2D> b)
        {
            if (PolygonsIntersect(a, b)) return 0;
            double best = double.MaxValue;
            for (int i = 0; i < a.Count; i++)
                for (int j = 0; j < b.Count; j++)
                    best = Math.Min(best, SegmentSegmentDistance(a[i], a[(i + 1) % a.Count], b[j], b[(j + 1) % b.Count]));
            return best;
        }

        /// <summary>
        /// Sutherland–Hodgman：把任意多邊形 subject 裁切到凸多邊形 clip 之內（交集）。
        /// 用來計算「梁、柱投影」與管道間重疊的部分。
        /// </summary>
        public static List<Point2D> ClipByConvex(IList<Point2D> subject, IList<Point2D> clip)
        {
            var output = new List<Point2D>(subject);
            var c = EnsureCcw(clip);
            for (int i = 0; i < c.Count && output.Count > 0; i++)
            {
                Point2D e1 = c[i], e2 = c[(i + 1) % c.Count];
                var input = output;
                output = new List<Point2D>();
                for (int j = 0; j < input.Count; j++)
                {
                    Point2D cur = input[j];
                    Point2D prev = input[(j + input.Count - 1) % input.Count];
                    bool curIn = Cross(e1, e2, cur) >= 0;
                    bool prevIn = Cross(e1, e2, prev) >= 0;
                    if (curIn)
                    {
                        if (!prevIn) output.Add(Intersect(prev, cur, e1, e2));
                        output.Add(cur);
                    }
                    else if (prevIn)
                    {
                        output.Add(Intersect(prev, cur, e1, e2));
                    }
                }
            }
            return output;
        }

        private static Point2D Intersect(Point2D a, Point2D b, Point2D e1, Point2D e2)
        {
            double t, u;
            if (!LineIntersection(a, b, e1, e2, out t, out u)) return b;
            return new Point2D(a.X + t * (b.X - a.X), a.Y + t * (b.Y - a.Y));
        }

        private static bool OnSegment(Point2D a, Point2D b, Point2D p)
        {
            return p.X >= Math.Min(a.X, b.X) - 1e-9 && p.X <= Math.Max(a.X, b.X) + 1e-9 &&
                   p.Y >= Math.Min(a.Y, b.Y) - 1e-9 && p.Y <= Math.Max(a.Y, b.Y) + 1e-9;
        }

        private static double Cross(Point2D o, Point2D a, Point2D b)
        {
            return (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        }
    }
}
