using System.Collections.Generic;

namespace ShaftChecker.Core.Recognition
{
    /// <summary>辨識結果等級。數字越大越有把握。</summary>
    public enum DetectionLevel
    {
        /// <summary>看起來不是管道間。</summary>
        None = 0,
        /// <summary>疑似：有一些線索，但需要人確認，預設不納入檢討。</summary>
        Candidate = 1,
        /// <summary>很可能：多項線索一致，預設納入檢討並標註為「推測」。</summary>
        Likely = 2,
        /// <summary>確定：名稱／編號／參數明確寫了管道間，或使用者已確認。</summary>
        Confirmed = 3
    }

    public static class DetectionLabels
    {
        public static string Of(DetectionLevel level)
        {
            switch (level)
            {
                case DetectionLevel.Confirmed: return "確定";
                case DetectionLevel.Likely: return "很可能";
                case DetectionLevel.Candidate: return "疑似";
                default: return "非管道間";
            }
        }
    }

    /// <summary>
    /// 一個空間（Room）可用來判斷「是不是管道間」的特徵。純資料，不依賴 Revit，方便單元測試。
    /// 由 Revit 端的 ShaftDetector 填入。
    /// </summary>
    public class SpaceFeatures
    {
        public long RoomId;
        public string SourceName;     // 位於連結模型時的連結名稱；主模型為 null
        public string Number;
        public string Name;
        public string LevelName;

        public double AreaM2;
        public double ShortSideMm;    // 最小外接矩形短邊
        public double LongSideMm;
        public double CentroidXMm;
        public double CentroidYMm;

        /// <summary>房間的某個文字參數（備註、部門、用途…）提到管道間時，記錄「參數名＝內容」。</summary>
        public string ParameterHit;

        /// <summary>使用者的明確決定："是"、"否"，未決定為 null。</summary>
        public string UserConfirm;

        public int DoorCount;

        /// <summary>落在此空間內的機電線性元件數（風管、水管、電管、電纜架）。</summary>
        public int MepLinearCount;
        /// <summary>其中近似垂直的數量。</summary>
        public int MepVerticalCount;
        /// <summary>落在此空間內的機電設備數（機械、電氣設備）。</summary>
        public int MepEquipmentCount;

        /// <summary>貫穿此位置的 Shaft Opening（垂直開口）數量。</summary>
        public int ShaftOpeningCount;

        /// <summary>其他樓層在相同位置有相近大小房間的樓層數（由 ShaftClassifier.AssignStacks 計算）。</summary>
        public int StackedLevels;
    }

    /// <summary>一個空間的辨識結果，含分數與判斷理由。</summary>
    public class ShaftDetection
    {
        public int Score;
        public DetectionLevel Level;
        public List<string> Reasons = new List<string>();

        /// <summary>是否由使用者確認／排除決定（不看分數）。</summary>
        public bool ByUser;
        public bool ExcludedByUser;

        /// <summary>是否有文字證據（名稱、編號、參數）。沒有文字證據時最高只會到「很可能」。</summary>
        public bool HasTextEvidence;

        public string LevelLabel
        {
            get { return DetectionLabels.Of(Level); }
        }

        /// <summary>預設是否納入檢討：確定、很可能納入；疑似需人工確認。</summary>
        public bool IncludedByDefault
        {
            get { return Level >= DetectionLevel.Likely; }
        }
    }
}
