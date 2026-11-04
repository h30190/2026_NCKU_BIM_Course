using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Recognition;

namespace ShaftChecker.Core.Models
{
    /// <summary>檢討結果等級。數字越大越嚴重，管道間的總結果取最嚴重者。</summary>
    public enum Severity
    {
        Pass = 0,
        Warning = 1,
        DataMissing = 2,
        Violation = 3
    }

    /// <summary>規則性質：法規強制項，或可調門檻的設計參數項。</summary>
    public enum RuleNature
    {
        Regulation,
        DesignParameter
    }

    public static class Labels
    {
        public static string Of(Severity s)
        {
            switch (s)
            {
                case Severity.Pass: return "合格";
                case Severity.Warning: return "警告";
                case Severity.DataMissing: return "資料不足";
                case Severity.Violation: return "違規";
                default: return s.ToString();
            }
        }

        public static string Of(RuleNature n)
        {
            return n == RuleNature.Regulation ? "法規" : "設計參數";
        }
    }

    /// <summary>管道間的邊界牆（只存檢討需要的資料）。</summary>
    public class WallInfo
    {
        public long Id;
        public string TypeName;
        /// <summary>防火時效的原始文字，例如「1 hr」「60 min」「1小時」；未填為 null。</summary>
        public string FireRatingRaw;
    }

    /// <summary>維修門前作業區內的障礙物。</summary>
    public class Obstruction
    {
        public long Id;
        public string Category;
        public string Name;
        /// <summary>障礙物位於連結模型時的連結名稱；主模型為 null。</summary>
        public string Source;
    }

    /// <summary>開向管道間的門（維修門）。</summary>
    public class DoorInfo
    {
        public long Id;
        public string TypeName;
        public string Mark;
        public string FireRatingRaw;
        /// <summary>遮煙性能：true/false；參數未建立或未填為 null。</summary>
        public bool? SmokeSeal;
        public double WidthMm;
        public double HeightMm;
        /// <summary>若門族群有「淨寬」參數則填入，否則 null。</summary>
        public double? ClearWidthMm;
        /// <summary>若門族群有「淨高」參數則填入，否則 null。</summary>
        public double? ClearHeightMm;
        /// <summary>若門族群有「門框寬度」參數則填入（mm），否則 null。</summary>
        public double? FrameWidthMm;
        /// <summary>門另一側的房間名稱（非管道間的那一側）。</summary>
        public string OtherSideRoomName;
        /// <summary>門另一側房間的「空間類型」參數內容（未填為 null）。</summary>
        public string OtherSideRoomType;
        public long? OtherSideRoomId;

        // ---- 門的平面位置（主模型座標 mm），A9、P3 使用 ----
        /// <summary>是否成功取得門的位置與方向。</summary>
        public bool HasGeometry;
        /// <summary>門中心（位於牆中心線上）。</summary>
        public double CenterX, CenterY;
        /// <summary>門檻（門底）標高。</summary>
        public double BaseZ;
        /// <summary>沿牆方向的單位向量。</summary>
        public double AlongX, AlongY;
        /// <summary>朝向管道間內部的單位向量。</summary>
        public double IntoX, IntoY;
        public double WallThicknessMm;
        /// <summary>門前作業區是否成功建立並檢查。</summary>
        public bool FrontCheckDone;
        public List<Obstruction> FrontObstructions = new List<Obstruction>();

        public string Label
        {
            get
            {
                string mark = string.IsNullOrEmpty(Mark) ? "" : "（" + Mark + "）";
                return (TypeName ?? "門") + mark;
            }
        }
    }

    /// <summary>一個管道間（一個 Room）整理好的純資料，規則只讀這個物件。</summary>
    public class ShaftInfo
    {
        public long RoomId;
        /// <summary>管道間所在的連結模型名稱；主模型為 null。連結模型中的元件無法寫回或上色。</summary>
        public string SourceName;
        /// <summary>穩定的房間鍵（連結名稱 + Room.UniqueId），給「確認是不是管道間」使用。</summary>
        public string RoomKey;
        /// <summary>這個空間為什麼被當成管道間（分數、等級、理由）。</summary>
        public ShaftDetection Detection;
        public string Number;
        public string Name;
        public string LevelName;
        /// <summary>完成面邊界（外圈），單位 mm。</summary>
        public List<Point2D> BoundaryMm = new List<Point2D>();
        public double AreaM2;
        public List<WallInfo> Walls = new List<WallInfo>();
        public List<DoorInfo> Doors = new List<DoorInfo>();

        // ---- 樓層資訊 ----
        /// <summary>本層樓板（房間底部）標高，主模型 mm。</summary>
        public double FloorZ;
        /// <summary>上一層樓板標高（沒有上一層時用房間頂部），主模型 mm。</summary>
        public double CeilingZ;
        /// <summary>位於地下層（樓層標高低於地面或名稱為 B1、地下…）。</summary>
        public bool IsUnderground;
        /// <summary>建築物屬高層建築物（16 層或 50 m 以上，或設定檔指定）。</summary>
        public bool IsHighRise;

        // ---- 管道間內與附近的機電、結構元件（主模型座標 mm）----
        public List<MepInfo> Mep = new List<MepInfo>();
        public List<BeamInfo> Beams = new List<BeamInfo>();
        public List<ColumnInfo> Columns = new List<ColumnInfo>();
        public List<InnerWallInfo> InnerWalls = new List<InnerWallInfo>();
        public List<AccessoryInfo> Accessories = new List<AccessoryInfo>();
        /// <summary>整個專案（含連結）是否找得到任何結構梁或柱。找不到時結構類規則無法檢討。</summary>
        public bool StructureModelFound = true;
        /// <summary>整個專案（含連結）是否找得到任何機電管線。</summary>
        public bool MepModelFound = true;
        /// <summary>機電／結構掃描是否完成；失敗不能當成附近沒有元件。</summary>
        public bool ScanComplete = true;

        /// <summary>由 EnsureMetrics() 計算：最小外接矩形短邊。</summary>
        public double ClearWidthMm;
        /// <summary>由 EnsureMetrics() 計算：最小外接矩形長邊。</summary>
        public double ClearDepthMm;
        private bool _metricsDone;

        public string Label
        {
            get { return (LevelName ?? "") + " " + (Number ?? "") + " " + (Name ?? ""); }
        }

        public void EnsureMetrics()
        {
            if (_metricsDone) return;
            RectResult r = PolygonUtils.MinAreaRectangle(BoundaryMm);
            ClearWidthMm = r.ShortSide;
            ClearDepthMm = r.LongSide;
            _metricsDone = true;
        }
    }

    /// <summary>元件參照：Id 與所在模型（主模型 Source = null，連結模型為連結名稱）。用來在 Revit 中選取與定位。</summary>
    public class ElementRef
    {
        public long Id;
        public string Source;

        public ElementRef(long id, string source)
        {
            Id = id;
            Source = source;
        }
    }

    /// <summary>一筆檢討結果。合格也會產生一筆（Severity.Pass），方便輸出完整報表。</summary>
    public class Issue
    {
        public string RuleId;
        public string RuleName;
        public RuleNature Nature;
        public string Reference;
        public long ShaftRoomId;
        public Severity Severity;
        public string Message;
        public double? Actual;
        public double? Required;
        public string Unit;
        /// <summary>改善建議（例如可移動方向與最小距離），沒有則為 null。</summary>
        public string Suggestion;
        /// <summary>相關元件（主模型 Id，用於上色）。</summary>
        public List<long> ElementIds = new List<long>();
        /// <summary>相關元件（含連結模型中的元件），用於在 Revit 中選取與定位。</summary>
        public List<ElementRef> Refs = new List<ElementRef>();
    }

    public class ShaftResult
    {
        public ShaftInfo Shaft;
        public List<Issue> Issues = new List<Issue>();
        public Severity Overall;
    }

    public class CheckResult
    {
        public List<ShaftResult> Shafts = new List<ShaftResult>();

        /// <summary>只含主模型中的管道間。連結模型（唯讀）裡的結果無法寫回參數、上色或選取。</summary>
        public CheckResult HostOnly()
        {
            var r = new CheckResult();
            r.Shafts.AddRange(Shafts.Where(s => s.Shaft.SourceName == null));
            return r;
        }

        public int LinkedCount
        {
            get { return Shafts.Count(s => s.Shaft.SourceName != null); }
        }
    }
}
