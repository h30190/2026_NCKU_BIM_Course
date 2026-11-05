namespace ShaftChecker.Core.Geometry
{
    /// <summary>平面座標點，單位 mm。</summary>
    public struct Point2D
    {
        public double X;
        public double Y;

        public Point2D(double x, double y)
        {
            X = x;
            Y = y;
        }

        public override string ToString()
        {
            return "(" + X.ToString("F0") + ", " + Y.ToString("F0") + ")";
        }
    }

    /// <summary>最小外接矩形的結果：短邊視為淨寬、長邊視為淨深。</summary>
    public struct RectResult
    {
        public double ShortSide;
        public double LongSide;
    }

    /// <summary>最小外接矩形的座標系：中心 (Cx, Cy)、長邊方向 U、短邊方向 V，半長 HalfU、半寬 HalfV。</summary>
    public struct RectFrame
    {
        public double Cx, Cy;
        public double Ux, Uy;
        public double Vx, Vy;
        public double HalfU, HalfV;

        /// <summary>主模型座標 → 本座標系 (u, v)。</summary>
        public Point2D ToLocal(double x, double y)
        {
            double dx = x - Cx, dy = y - Cy;
            return new Point2D(dx * Ux + dy * Uy, dx * Vx + dy * Vy);
        }

        /// <summary>本座標系 (u, v) → 主模型座標。</summary>
        public Point2D ToWorld(double u, double v)
        {
            return new Point2D(Cx + u * Ux + v * Vx, Cy + u * Uy + v * Vy);
        }
    }
}
