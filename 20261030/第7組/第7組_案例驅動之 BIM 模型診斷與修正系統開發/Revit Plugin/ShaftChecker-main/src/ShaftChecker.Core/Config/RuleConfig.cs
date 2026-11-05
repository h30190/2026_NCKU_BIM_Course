using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Text.RegularExpressions;

namespace ShaftChecker.Core.Config
{
    /// <summary>
    /// 檢討規則設定檔（config/shaft-rules.json）。
    /// 使用 .NET 內建的 DataContractJsonSerializer，不需要額外套件，避免與 Revit 內建 DLL 版本衝突。
    /// JSON 中沒寫的欄位會使用預設值。
    /// </summary>
    [DataContract]
    public class RuleConfig
    {
        [DataMember(Name = "shaftNamePatterns")] public List<string> ShaftNamePatterns;
        [DataMember(Name = "stairRoomPatterns")] public List<string> StairRoomPatterns;
        [DataMember(Name = "commonRoomPatterns")] public List<string> CommonRoomPatterns;

        // ---- 管道間辨識（多訊號評分）----
        /// <summary>房間任一文字參數（備註、部門、用途…）符合這些就算有「提到管道間」。</summary>
        [DataMember(Name = "shaftKeywordPatterns")] public List<string> ShaftKeywordPatterns;
        /// <summary>名稱符合這些，代表明確是其他空間（辦公室、廁所、走廊…），辨識時扣分。</summary>
        [DataMember(Name = "nonShaftNamePatterns")] public List<string> NonShaftNamePatterns;
        [DataMember(Name = "detectConfirmedScore")] public int DetectConfirmedScore;
        [DataMember(Name = "detectLikelyScore")] public int DetectLikelyScore;
        [DataMember(Name = "detectCandidateScore")] public int DetectCandidateScore;
        /// <summary>超過這個面積的空間（沒有明確名稱時）不會被判成管道間。</summary>
        [DataMember(Name = "maxShaftAreaM2")] public double MaxShaftAreaM2;
        /// <summary>是否把「疑似」的空間也納入檢討（預設否，需使用者確認）。</summary>
        [DataMember(Name = "includeCandidateShafts")] public bool IncludeCandidateShafts;

        [DataMember(Name = "fireRatingParam")] public string FireRatingParam;
        [DataMember(Name = "smokeSealParam")] public string SmokeSealParam;
        [DataMember(Name = "doorClearWidthParam")] public string DoorClearWidthParam;
        [DataMember(Name = "doorClearHeightParam")] public string DoorClearHeightParam;
        /// <summary>門框寬度參數（依序嘗試）。有填時，淨寬 = 寬度 − 2 × 門框寬度、淨高 = 高度 − 門框寬度。</summary>
        [DataMember(Name = "doorFrameWidthParams")] public List<string> DoorFrameWidthParams;
        /// <summary>房間「空間類型」參數名稱（依序嘗試），A4 用來判斷門另一側是否為樓梯間或排煙室。</summary>
        [DataMember(Name = "roomTypeParams")] public List<string> RoomTypeParams;

        // ---- 樓層判斷 ----
        /// <summary>樓層名稱符合這些視為地下層（另外，標高低於 basementElevationMm 也視為地下層）。</summary>
        [DataMember(Name = "basementLevelPatterns")] public List<string> BasementLevelPatterns;
        [DataMember(Name = "basementElevationMm")] public double BasementElevationMm;
        /// <summary>高層建築物判斷："auto"（16 層或 50 m 以上）、"yes"、"no"。</summary>
        [DataMember(Name = "highRiseMode")] public string HighRiseMode;
        [DataMember(Name = "highRiseFloors")] public int HighRiseFloors;
        [DataMember(Name = "highRiseHeightMm")] public double HighRiseHeightMm;

        // ---- 管線分類 ----
        [DataMember(Name = "gasSystemPatterns")] public List<string> GasSystemPatterns;
        [DataMember(Name = "fireSystemPatterns")] public List<string> FireSystemPatterns;
        [DataMember(Name = "hvacWaterPatterns")] public List<string> HvacWaterPatterns;
        [DataMember(Name = "plumbingPatterns")] public List<string> PlumbingPatterns;
        /// <summary>高層建築物中，可與瓦斯管同在一個管道間的系統（中文標籤：給排水、消防、空調水、電氣、風管、其他管）。空清單 = 瓦斯管道間必須單獨設置。</summary>
        [DataMember(Name = "gasAllowedGroups")] public List<string> GasAllowedGroups;
        /// <summary>防火閘門的族群／類型名稱關鍵字。</summary>
        [DataMember(Name = "fireDamperPatterns")] public List<string> FireDamperPatterns;
        /// <summary>防火閘門的「是／否」參數名稱（族群名稱沒有關鍵字時改看這個參數）。</summary>
        [DataMember(Name = "fireDamperParam")] public string FireDamperParam;

        [DataMember(Name = "minFireRatingHours")] public double MinFireRatingHours;
        [DataMember(Name = "minClearWidthMm")] public double MinClearWidthMm;
        [DataMember(Name = "minClearAreaM2")] public double MinClearAreaM2;
        [DataMember(Name = "minDoorClearWidthMm")] public double MinDoorClearWidthMm;
        [DataMember(Name = "minDoorHeightMm")] public double MinDoorHeightMm;
        [DataMember(Name = "doorFrameDeductionMm")] public double DoorFrameDeductionMm;
        [DataMember(Name = "frontClearanceDepthMm")] public double FrontClearanceDepthMm;
        [DataMember(Name = "frontClearanceHeightMm")] public double FrontClearanceHeightMm;
        [DataMember(Name = "minClearDepthMm")] public double MinClearDepthMm;
        /// <summary>沒有淨高參數也沒有門框寬度參數時，以高度扣除此值推算淨高。</summary>
        [DataMember(Name = "doorFrameHeadDeductionMm")] public double DoorFrameHeadDeductionMm;
        /// <summary>A9：梁底至少要比門頂高出這個值（門框上檻、門弓器等）。</summary>
        [DataMember(Name = "doorHeadAllowanceMm")] public double DoorHeadAllowanceMm;

        // ---- 管線分隔與間距 ----
        [DataMember(Name = "minPipeClearMm")] public double MinPipeClearMm;
        [DataMember(Name = "doorBackClearDepthMm")] public double DoorBackClearDepthMm;
        [DataMember(Name = "damperSearchMm")] public double DamperSearchMm;

        // ---- 結構柱梁 ----
        /// <summary>梁柱侵入管道間的深度小於此值視為建模誤差，不判違規。</summary>
        [DataMember(Name = "structOverlapTolMm")] public double StructOverlapTolMm;
        /// <summary>管道間周圍搜尋梁、柱、管線的距離。</summary>
        [DataMember(Name = "structSearchMm")] public double StructSearchMm;
        /// <summary>建議移動距離的上限，超過就不列為建議。</summary>
        [DataMember(Name = "maxMoveSearchMm")] public double MaxMoveSearchMm;
        /// <summary>穿梁孔徑上限：孔徑 ≤ 梁深 × 此值。</summary>
        [DataMember(Name = "beamHoleMaxRatio")] public double BeamHoleMaxRatio;
        /// <summary>孔中心須位於梁深中央的這個比例範圍內（1/3 = 中央三分之一）。</summary>
        [DataMember(Name = "beamHoleCenterBandRatio")] public double BeamHoleCenterBandRatio;
        /// <summary>孔距支承面（柱面）至少 = 梁深 × 此值。</summary>
        [DataMember(Name = "beamHoleEndDistFactor")] public double BeamHoleEndDistFactor;
        /// <summary>同一根梁上相鄰兩孔中心距至少 = 平均孔徑 × 此值。</summary>
        [DataMember(Name = "beamHoleSpacingFactor")] public double BeamHoleSpacingFactor;
        /// <summary>管線改走梁下時，管頂與梁底的預留距離。</summary>
        [DataMember(Name = "pipeBeamClearMm")] public double PipeBeamClearMm;

        /// <summary>門前作業區要檢查的 Revit 類別（BuiltInCategory 名稱）。</summary>
        [DataMember(Name = "obstacleCategories")] public List<string> ObstacleCategories;

        /// <summary>要執行的規則；空清單代表全部執行。</summary>
        [DataMember(Name = "enabledRules")] public List<string> EnabledRules;

        /// <summary>AI 連線（本機 HTTP）埠號。</summary>
        [DataMember(Name = "bridgePort")] public int BridgePort;

        public RuleConfig()
        {
            SetDefaults();
        }

        // DataContract 反序列化不會呼叫建構子，所以在反序列化前先填預設值
        [OnDeserializing]
        private void OnDeserializing(StreamingContext context)
        {
            SetDefaults();
        }

        private void SetDefaults()
        {
            ShaftNamePatterns = new List<string> { "^PS", "^DS", "^EPS", "^WS", "管道", "管井", "設備井", "電氣井", "shaft" };
            StairRoomPatterns = new List<string> { "樓梯", "梯間", "排煙室", "安全梯", "^STAIR" };
            CommonRoomPatterns = new List<string> { "走廊", "梯廳", "門廳", "機房", "^CORR" };

            ShaftKeywordPatterns = new List<string> { "管道", "管井", "設備井", "電氣井", "通風井", "排煙井", "shaft", "riser" };
            NonShaftNamePatterns = new List<string>
            {
                "辦公", "廁", "浴", "盥洗", "梯", "走廊", "走道", "大廳", "門廳", "機房", "機電室", "配電室",
                "會議", "儲藏", "倉", "茶水", "客廳", "餐", "廚", "臥", "展示", "教室",
                @"\b(office|toilet|restroom|wc|corridor|hall|lobby|stair|stairs|elevator|lift|meeting|storage|kitchen|bedroom|living)\b"
            };
            DetectConfirmedScore = 90;
            DetectLikelyScore = 60;
            DetectCandidateScore = 40;
            MaxShaftAreaM2 = 12;
            IncludeCandidateShafts = false;

            FireRatingParam = "防火時效";
            SmokeSealParam = "遮煙性能";
            DoorClearWidthParam = "淨寬";
            DoorClearHeightParam = "淨高";
            DoorFrameWidthParams = new List<string> { "門框寬度", "框寬", "Frame Width" };
            RoomTypeParams = new List<string> { "空間類型", "Space Type", "Room Type", "用途" };

            BasementLevelPatterns = new List<string> { @"^B\d", "^地下", "地下室", "basement" };
            BasementElevationMm = -500;
            HighRiseMode = "auto";
            HighRiseFloors = 16;
            HighRiseHeightMm = 50000;

            GasSystemPatterns = new List<string> { "瓦斯", "燃氣", "天然氣", "液化石油", @"\bgas\b", "LPG" };
            FireSystemPatterns = new List<string> { "消防", "撒水", "灑水", "消火栓", "泡沫", "sprinkler", "fire" };
            HvacWaterPatterns = new List<string> { "冰水", "冷卻水", "冷凍水", "空調", "hydronic", "chilled", "condenser", "循環供水", "循環回水" };
            PlumbingPatterns = new List<string>
            {
                "給水", "排水", "污水", "雜排", "廢水", "通氣", "雨水", "冷水", "熱水", "衛生", "通風口", "家庭用", "自來水",
                "domestic", "sanitary", "vent", "storm", "rain", "waste", "soil", "water"
            };
            GasAllowedGroups = new List<string> { "給排水" };
            FireDamperPatterns = new List<string> { "防火閘", "防火風門", "防煙閘", @"\bFD\b", @"\bFSD\b", "fire.?damper", "smoke.?damper" };
            FireDamperParam = "防火閘門";

            MinFireRatingHours = 1.0;
            MinClearWidthMm = 600;
            MinClearAreaM2 = 0;          // 0 = 不檢查面積
            MinDoorClearWidthMm = 600;
            MinDoorHeightMm = 1800;
            DoorFrameDeductionMm = 0;    // 沒有淨寬參數時，以寬度扣除此值推算淨寬
            FrontClearanceDepthMm = 750;
            FrontClearanceHeightMm = 2000;
            MinClearDepthMm = 0;         // 0 = 不檢查淨深
            DoorFrameHeadDeductionMm = 0;
            DoorHeadAllowanceMm = 50;

            MinPipeClearMm = 50;
            DoorBackClearDepthMm = 600;
            DamperSearchMm = 1000;

            StructOverlapTolMm = 20;
            StructSearchMm = 1500;
            MaxMoveSearchMm = 3000;
            BeamHoleMaxRatio = 1.0 / 3.0;
            BeamHoleCenterBandRatio = 1.0 / 3.0;
            BeamHoleEndDistFactor = 2.0;
            BeamHoleSpacingFactor = 3.0;
            PipeBeamClearMm = 50;

            ObstacleCategories = new List<string>
            {
                "OST_Walls", "OST_Columns", "OST_StructuralColumns", "OST_StructuralFraming",
                "OST_Furniture", "OST_Casework", "OST_MechanicalEquipment", "OST_ElectricalEquipment",
                "OST_PlumbingFixtures", "OST_GenericModel"
            };

            EnabledRules = new List<string>();
            BridgePort = 5099;
        }

        /// <summary>複製一份設定，供 AI 的「假設分析」臨時修改，不影響設定檔。</summary>
        public RuleConfig Clone()
        {
            var c = (RuleConfig)MemberwiseClone();
            c.ShaftNamePatterns = new List<string>(ShaftNamePatterns ?? new List<string>());
            c.StairRoomPatterns = new List<string>(StairRoomPatterns ?? new List<string>());
            c.CommonRoomPatterns = new List<string>(CommonRoomPatterns ?? new List<string>());
            c.ShaftKeywordPatterns = new List<string>(ShaftKeywordPatterns ?? new List<string>());
            c.NonShaftNamePatterns = new List<string>(NonShaftNamePatterns ?? new List<string>());
            c.ObstacleCategories = new List<string>(ObstacleCategories ?? new List<string>());
            c.DoorFrameWidthParams = new List<string>(DoorFrameWidthParams ?? new List<string>());
            c.RoomTypeParams = new List<string>(RoomTypeParams ?? new List<string>());
            c.BasementLevelPatterns = new List<string>(BasementLevelPatterns ?? new List<string>());
            c.GasSystemPatterns = new List<string>(GasSystemPatterns ?? new List<string>());
            c.FireSystemPatterns = new List<string>(FireSystemPatterns ?? new List<string>());
            c.HvacWaterPatterns = new List<string>(HvacWaterPatterns ?? new List<string>());
            c.PlumbingPatterns = new List<string>(PlumbingPatterns ?? new List<string>());
            c.GasAllowedGroups = new List<string>(GasAllowedGroups ?? new List<string>());
            c.FireDamperPatterns = new List<string>(FireDamperPatterns ?? new List<string>());
            c.EnabledRules = new List<string>(EnabledRules ?? new List<string>());
            return c;
        }

        /// <summary>可被臨時覆寫的門檻（名稱與 JSON 欄位相同）。</summary>
        public static readonly string[] OverridableKeys =
        {
            "minFireRatingHours", "minClearWidthMm", "minClearAreaM2", "minDoorClearWidthMm",
            "minDoorHeightMm", "doorFrameDeductionMm", "frontClearanceDepthMm", "frontClearanceHeightMm",
            "minClearDepthMm", "doorHeadAllowanceMm", "minPipeClearMm", "doorBackClearDepthMm", "damperSearchMm",
            "structOverlapTolMm", "beamHoleMaxRatio", "beamHoleEndDistFactor", "beamHoleSpacingFactor"
        };

        /// <summary>套用一個臨時門檻。回傳 false 代表不認得這個欄位。</summary>
        public bool ApplyOverride(string key, double value)
        {
            switch ((key ?? "").Trim())
            {
                case "minFireRatingHours": MinFireRatingHours = value; return true;
                case "minClearWidthMm": MinClearWidthMm = value; return true;
                case "minClearAreaM2": MinClearAreaM2 = value; return true;
                case "minDoorClearWidthMm": MinDoorClearWidthMm = value; return true;
                case "minDoorHeightMm": MinDoorHeightMm = value; return true;
                case "doorFrameDeductionMm": DoorFrameDeductionMm = value; return true;
                case "frontClearanceDepthMm": FrontClearanceDepthMm = value; return true;
                case "frontClearanceHeightMm": FrontClearanceHeightMm = value; return true;
                case "minClearDepthMm": MinClearDepthMm = value; return true;
                case "doorHeadAllowanceMm": DoorHeadAllowanceMm = value; return true;
                case "minPipeClearMm": MinPipeClearMm = value; return true;
                case "doorBackClearDepthMm": DoorBackClearDepthMm = value; return true;
                case "damperSearchMm": DamperSearchMm = value; return true;
                case "structOverlapTolMm": StructOverlapTolMm = value; return true;
                case "beamHoleMaxRatio": BeamHoleMaxRatio = value; return true;
                case "beamHoleEndDistFactor": BeamHoleEndDistFactor = value; return true;
                case "beamHoleSpacingFactor": BeamHoleSpacingFactor = value; return true;
                default: return false;
            }
        }

        /// <summary>目前所有門檻，供 /rules 端點回報給 AI。</summary>
        public Dictionary<string, double> Thresholds()
        {
            return new Dictionary<string, double>
            {
                { "minFireRatingHours", MinFireRatingHours },
                { "minClearWidthMm", MinClearWidthMm },
                { "minClearAreaM2", MinClearAreaM2 },
                { "minDoorClearWidthMm", MinDoorClearWidthMm },
                { "minDoorHeightMm", MinDoorHeightMm },
                { "doorFrameDeductionMm", DoorFrameDeductionMm },
                { "frontClearanceDepthMm", FrontClearanceDepthMm },
                { "frontClearanceHeightMm", FrontClearanceHeightMm },
                { "minClearDepthMm", MinClearDepthMm },
                { "doorHeadAllowanceMm", DoorHeadAllowanceMm },
                { "minPipeClearMm", MinPipeClearMm },
                { "doorBackClearDepthMm", DoorBackClearDepthMm },
                { "damperSearchMm", DamperSearchMm },
                { "structOverlapTolMm", StructOverlapTolMm },
                { "beamHoleMaxRatio", BeamHoleMaxRatio },
                { "beamHoleEndDistFactor", BeamHoleEndDistFactor },
                { "beamHoleSpacingFactor", BeamHoleSpacingFactor }
            };
        }

        public bool IsEnabled(string ruleId)
        {
            return EnabledRules == null || EnabledRules.Count == 0 ||
                   EnabledRules.Any(r => string.Equals(r, ruleId, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>文字是否符合任一正規表示式（不分大小寫）。</summary>
        public static bool MatchesAny(string text, IEnumerable<string> patterns)
        {
            if (string.IsNullOrEmpty(text) || patterns == null) return false;
            foreach (var p in patterns)
            {
                if (string.IsNullOrEmpty(p)) continue;
                if (Regex.IsMatch(text, p, RegexOptions.IgnoreCase)) return true;
            }
            return false;
        }

        public static RuleConfig Load(string path)
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return new RuleConfig();

            byte[] bytes = File.ReadAllBytes(path);
            var serializer = new DataContractJsonSerializer(typeof(RuleConfig));
            using (var ms = new MemoryStream(StripBom(bytes)))
            {
                var cfg = (RuleConfig)serializer.ReadObject(ms);
                return cfg ?? new RuleConfig();
            }
        }

        private static byte[] StripBom(byte[] bytes)
        {
            byte[] bom = Encoding.UTF8.GetPreamble();
            if (bytes.Length >= 3 && bytes[0] == bom[0] && bytes[1] == bom[1] && bytes[2] == bom[2])
                return bytes.Skip(3).ToArray();
            return bytes;
        }
    }
}
