using System;
using System.Collections.Generic;
using ShaftChecker.Core.Geometry;

namespace ShaftChecker.Core.Models
{
    /// <summary>機電線性元件的種類。</summary>
    public enum MepKind
    {
        Pipe,
        Duct,
        Conduit,
        CableTray
    }

    /// <summary>
    /// 管線系統分群（依建築設備編第 2-1 條的分隔要求與高層專章的瓦斯管規定）。
    /// </summary>
    public enum MepGroup
    {
        /// <summary>電氣類：電管（Conduit）、電纜架（Cable Tray）。</summary>
        Electrical,
        /// <summary>給排水：給水、熱水、排水、污水、通氣、雨水。</summary>
        Plumbing,
        /// <summary>消防水管。</summary>
        FireProtection,
        /// <summary>空調用水管（冰水、冷卻水、熱水循環）。</summary>
        HvacWater,
        /// <summary>瓦斯（燃氣）管。</summary>
        Gas,
        /// <summary>風管。</summary>
        Air,
        /// <summary>無法判斷系統的水管。</summary>
        OtherPipe
    }

    public static class MepLabels
    {
        public static string Of(MepGroup g)
        {
            switch (g)
            {
                case MepGroup.Electrical: return "電氣";
                case MepGroup.Plumbing: return "給排水";
                case MepGroup.FireProtection: return "消防";
                case MepGroup.HvacWater: return "空調水";
                case MepGroup.Gas: return "瓦斯";
                case MepGroup.Air: return "風管";
                default: return "其他管";
            }
        }

        public static string Of(MepKind k)
        {
            switch (k)
            {
                case MepKind.Pipe: return "水管";
                case MepKind.Duct: return "風管";
                case MepKind.Conduit: return "電管";
                default: return "電纜架";
            }
        }

        /// <summary>依中文標籤找回分群（設定檔用）。找不到回傳 null。</summary>
        public static MepGroup? Parse(string label)
        {
            foreach (MepGroup g in Enum.GetValues(typeof(MepGroup)))
            {
                if (string.Equals(Of(g), (label ?? "").Trim(), StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(g.ToString(), (label ?? "").Trim(), StringComparison.OrdinalIgnoreCase))
                    return g;
            }
            return null;
        }
    }

    /// <summary>
    /// 一段機電線性元件（水管、風管、電管、電纜架），座標為主模型座標（mm），中心線兩端點。
    /// </summary>
    public class MepInfo
    {
        public long Id;
        /// <summary>連結模型名稱；主模型為 null。</summary>
        public string Source;
        public MepKind Kind;
        public MepGroup Group;
        /// <summary>系統名稱／系統類型／管類型（給人看的說明）。</summary>
        public string SystemName;
        public double X1, Y1, Z1, X2, Y2, Z2;
        /// <summary>圓形斷面外徑（mm）；矩形斷面為 0。</summary>
        public double OuterDiameterMm;
        /// <summary>矩形斷面寬、高（mm）；圓形斷面為 0。</summary>
        public double WidthMm, HeightMm;
        /// <summary>保溫層厚度（mm），沒有保溫為 0。</summary>
        public double InsulationMm;
        /// <summary>垂直矩形元件的平面外框（主模型 mm），圓形或水平元件為 null。</summary>
        public List<Point2D> FootprintMm;

        public bool IsRound { get { return OuterDiameterMm > 0 || (WidthMm <= 0 && HeightMm <= 0); } }

        public double LengthMm
        {
            get
            {
                double dx = X2 - X1, dy = Y2 - Y1, dz = Z2 - Z1;
                return Math.Sqrt(dx * dx + dy * dy + dz * dz);
            }
        }

        /// <summary>中心線與鉛垂線夾角小於約 15° 視為立管。</summary>
        public bool IsVertical
        {
            get
            {
                double len = LengthMm;
                if (len < 1) return false;
                return Math.Abs(Z2 - Z1) >= 0.966 * len;
            }
        }

        /// <summary>斷面的「外半徑」（含保溫）：圓形 = 外徑/2 + 保溫；矩形 = 寬高較大者/2 + 保溫。</summary>
        public double OuterRadiusMm
        {
            get
            {
                double r = OuterDiameterMm > 0 ? OuterDiameterMm / 2 : Math.Max(WidthMm, HeightMm) / 2;
                return r + InsulationMm;
            }
        }

        /// <summary>斷面在鉛垂方向的高度（含保溫），穿梁孔徑檢討用。</summary>
        public double VerticalSizeMm
        {
            get
            {
                double h = OuterDiameterMm > 0 ? OuterDiameterMm : HeightMm;
                return h + 2 * InsulationMm;
            }
        }

        public double MinZ { get { return Math.Min(Z1, Z2); } }
        public double MaxZ { get { return Math.Max(Z1, Z2); } }

        /// <summary>給人看的簡短名稱，例如「給排水 水管 Ø100 #1234」。</summary>
        public string Label
        {
            get
            {
                string size = OuterDiameterMm > 0
                    ? "Ø" + OuterDiameterMm.ToString("F0")
                    : WidthMm.ToString("F0") + "×" + HeightMm.ToString("F0");
                string name;
                if (Kind == MepKind.Pipe) name = Group == MepGroup.OtherPipe ? "水管" : MepLabels.Of(Group) + "管";
                else name = MepLabels.Of(Kind); // 電管、電纜架、風管
                return name + " " + size + " #" + Id + (string.IsNullOrEmpty(Source) ? "" : "（連結）");
            }
        }
    }

    /// <summary>
    /// 結構梁。平面投影以中心軸線 + 寬度表示；梁底、梁頂為主模型標高（mm）。
    /// </summary>
    public class BeamInfo
    {
        public long Id;
        public string Source;
        public string Name;
        public double X1, Y1, X2, Y2;
        public double WidthMm;
        public double BottomZ;
        public double TopZ;

        public double DepthMm { get { return TopZ - BottomZ; } }

        public double LengthMm
        {
            get { return Math.Sqrt((X2 - X1) * (X2 - X1) + (Y2 - Y1) * (Y2 - Y1)); }
        }

        /// <summary>平面投影的矩形（逆時針）。</summary>
        public List<Point2D> Footprint()
        {
            return PolygonUtils.RectangleFromAxis(new Point2D(X1, Y1), new Point2D(X2, Y2), WidthMm);
        }

        public string Label
        {
            get { return "梁「" + (Name ?? "") + "」" + (string.IsNullOrEmpty(Source) ? "" : "（連結）"); }
        }
    }

    /// <summary>結構柱或建築柱。平面投影為凸多邊形。</summary>
    public class ColumnInfo
    {
        public long Id;
        public string Source;
        public string Name;
        public List<Point2D> FootprintMm = new List<Point2D>();
        public double BottomZ;
        public double TopZ;

        public string Label
        {
            get { return "柱「" + (Name ?? "") + "」" + (string.IsNullOrEmpty(Source) ? "" : "（連結）"); }
        }
    }

    /// <summary>管道間附近的牆（只取直牆的中心線與厚度），用來判斷管道間內部是否有隔牆。</summary>
    public class InnerWallInfo
    {
        public long Id;
        public string Source;
        public double X1, Y1, X2, Y2;
        public double ThicknessMm;
        public double BottomZ;
        public double TopZ;
    }

    /// <summary>風管附件（防火閘門等）。</summary>
    public class AccessoryInfo
    {
        public long Id;
        public string Source;
        public string Name;
        public double X, Y, Z;
        public bool IsFireDamper;
    }
}
