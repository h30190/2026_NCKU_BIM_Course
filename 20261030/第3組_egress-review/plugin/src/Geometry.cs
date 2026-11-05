using System;
using System.Collections.Generic;
using System.Linq;

namespace Group3.EgressExporter
{
    public static class Geometry
    {
        public const double Tolerance = 0.001; // Geometry join tolerance: 1 mm, not a legal threshold.
        public static double Distance(double[] a, double[] b) =>
            Math.Sqrt(Math.Pow(a[0] - b[0], 2) + Math.Pow(a[1] - b[1], 2));
        public static double Area(List<double[]> p) => Math.Abs(p.Select((a, i) =>
            a[0] * p[(i + 1) % p.Count][1] - p[(i + 1) % p.Count][0] * a[1]).Sum()) / 2;
        public static List<double[]> Ring(IEnumerable<double[]> points)
        {
            var result = new List<double[]>();
            foreach (var p in points)
                if (result.Count == 0 || Distance(result[result.Count - 1], p) > Tolerance) result.Add(p);
            if (result.Count > 1 && Distance(result[0], result[result.Count - 1]) <= Tolerance)
                result.RemoveAt(result.Count - 1);
            return result;
        }
        // Revit 把房間內的柱子、孔洞回傳成額外的邊界圈。面積最大的是外框，其他圈完全在外框內才算內圈；
        // 有圈落在外框外（不相連的區域）回傳 null，不猜測路線。
        public static List<double[]> OuterLoop(List<List<double[]>> loops, out List<List<double[]>> holes)
        {
            holes = new List<List<double[]>>();
            if (loops == null || loops.Count == 0) return null;
            var outer = loops.OrderByDescending(Area).First();
            foreach (var loop in loops)
            {
                if (ReferenceEquals(loop, outer)) continue;
                if (loop.Any(p => !Contains(outer, p))) return null;
                holes.Add(loop);
            }
            return outer;
        }
        static double Cross(double ax, double ay, double bx, double by) => ax * by - ay * bx;
        public static bool Contains(List<double[]> ring, double[] p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[j]; var b = ring[i];
                double length = Distance(a, b);
                if (length > 0 && Math.Abs(Cross(b[0]-a[0], b[1]-a[1], p[0]-a[0], p[1]-a[1])) / length <= 1e-8 &&
                    p[0] >= Math.Min(a[0],b[0])-1e-8 && p[0] <= Math.Max(a[0],b[0])+1e-8 &&
                    p[1] >= Math.Min(a[1],b[1])-1e-8 && p[1] <= Math.Max(a[1],b[1])+1e-8) return true;
                if ((a[1] > p[1]) != (b[1] > p[1]) && p[0] < (b[0]-a[0])*(p[1]-a[1])/(b[1]-a[1])+a[0]) inside = !inside;
            }
            return inside;
        }
        public static bool ContainsPath(List<double[]> ring, List<double[]> path)
        {
            if (ring.Count < 3 || path.Count < 2 || path.Any(p => !Contains(ring, p))) return false;
            for (int i = 1; i < path.Count; i++)
            {
                var a = path[i-1]; var b = path[i];
                double dx = b[0]-a[0], dy = b[1]-a[1];
                var cuts = new List<double> { 0, 1 };
                for (int k = 0; k < ring.Count; k++)
                {
                    var c = ring[k]; var d = ring[(k+1)%ring.Count];
                    double ex = d[0]-c[0], ey = d[1]-c[1], determinant = Cross(dx,dy,ex,ey);
                    if (Math.Abs(determinant) < 1e-12) continue;
                    double t = Cross(c[0]-a[0],c[1]-a[1],ex,ey)/determinant;
                    double u = Cross(c[0]-a[0],c[1]-a[1],dx,dy)/determinant;
                    if (t > 0 && t < 1 && u >= 0 && u <= 1) cuts.Add(t);
                }
                cuts.Sort();
                for (int k = 1; k < cuts.Count; k++) {
                    double t = (cuts[k-1]+cuts[k])/2;
                    if (!Contains(ring, new[] { a[0]+t*dx, a[1]+t*dy })) return false;
                }
            }
            return true;
        }
        // Input order is user-defined. Reject disconnected curves; never bridge a gap through a wall.
        public static List<double[]> Chain(List<List<double[]>> parts)
        {
            if (parts.Count == 0 || parts.Any(p => p.Count < 2)) throw new ArgumentException("中心線至少需要一段有效曲線。");
            foreach (var reverseFirst in new[] { false, true })
            {
                var result = new List<double[]>(parts[0]);
                if (reverseFirst) result.Reverse();
                bool valid = true;
                foreach (var source in parts.Skip(1))
                {
                    var part = new List<double[]>(source);
                    var end = result[result.Count - 1];
                    if (Distance(end, part[0]) > Tolerance) part.Reverse();
                    if (Distance(end, part[0]) > Tolerance) { valid = false; break; }
                    result.AddRange(part.Skip(1));
                }
                if (valid) return result;
            }
            throw new ArgumentException("走廊曲線未依序連接，或接點差距大於 1 mm。");
        }
    }
}
