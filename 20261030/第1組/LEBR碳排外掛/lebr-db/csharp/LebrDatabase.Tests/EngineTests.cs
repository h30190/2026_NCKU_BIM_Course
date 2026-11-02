using System;
using System.Linq;
using LebrCarbon;

namespace LebrDatabaseTests
{
    /// <summary>計算引擎的測試（需要 LebrEngine.cs 與 samples/appendix3_quantities.json）。資料庫交接包不含此檔。</summary>
    internal static partial class Program
    {
        /// <summary>用手冊附錄三（p.65～93）的數量重現手冊的計算結果。</summary>
        static partial void TestEngine(LebrDatabase db, string samplePath)
        {
            TestProjectSettingsRules(db);
            TestBaselineIndependentOfDesignSpF(db, samplePath);

            var calc = new LebrCalculator(db);
            var r = calc.Calculate(QuantityInput.Load(samplePath));

            Section("計算引擎：手冊附錄三範例");
            Equal("所有必要項目都有計算", true, r.Complete);
            Equal("沒有未對應的構件", 0, r.Unmapped.Count);
            Within("設計案主結構密度 C（手冊印 251.33，式 4 重算 251.26）", 251.26, r.Structure.DesignC, 0.01);
            Within("基準案主結構密度 C", 294.42, r.Structure.BaselineC, 0.01);
            Near("LCCR = 1 − 1.7 × 0.05", 0.915, r.Structure.Lccr);
            Relative("設計案主結構 CFs", 12934263, r.Design.CFs);
            Relative("基準案主結構 CFsc", 16563985, r.Baseline.CFs);
            Relative("設計案非結構 CFns", 7204541, r.Design.CFns);
            Relative("基準案非結構 CFns", 8342492, r.Baseline.CFns);
            Relative("設計案製造運輸 CFum", 20138804, r.Design.CFum);
            Relative("基準案製造運輸 CFum", 24906477, r.Baseline.CFum);
            Relative("設計案更新修繕 CFrm", 1476954, r.Design.CFrm);
            Relative("基準案更新修繕 CFrm", 1444166, r.Baseline.CFrm);
            Relative("施工 CFc（兩案相同）", 811585, r.Design.CFc);
            Relative("拆除廢棄 CFdw（兩案相同）", 1467375, r.Design.CFdw);
            Relative("設計案 EEC", 22756875, r.EEC);
            Relative("基準案 EECc", 28629604, r.EECc);
            Within("減碳率 CFR 20.51%", 0.2051, r.CFR.Value, 0.0005);
            Equal("等級", "1+", r.Grade);
            Within("ECI", 404.50, r.ECI, 0.5);
            Within("ECIs", 508.88, r.ECIs, 0.5);
            Relative("地下主結構 CFs'", 11241565, r.UndergroundStructure);
            Relative("地下施工 CFc'", 520735, r.UndergroundConstruction);
            Relative("地下拆除 CFdw'", 1274170, r.UndergroundDemolition);
            Relative("全生命週期 TEC", 35793345, r.TEC);
            Equal("代號 IW-02 依低耗損補成 IW-L-02", true, r.Items.Any(i => i.Code == "IW-L-02"));
            Equal("結果 JSON 可以解析", true, Newtonsoft.Json.Linq.JObject.Parse(r.ToJson())["result"]["grade"].ToString() == "1+");
            var json = Newtonsoft.Json.Linq.JObject.Parse(r.ToJson());
            Equal("結果 JSON 主結構方法為標準公式", "standard", (string)json["structure"]["method"]);
            Within("結果 JSON 主結構設計案 C", 251.26, (double)json["structure"]["standard"]["C"], 0.01);
            Near("結果 JSON 主結構基準案 Sp（表 4-6）", 1.8, (double)json["structure"]["standard"]["baseline_Sp"]);
            Near("結果 JSON 主結構 LCCR", 0.915, (double)json["structure"]["standard"]["LCCR"]);
            Near("結果 JSON 主結構 CFs 與引擎一致", r.Design.CFs, (double)json["structure"]["design_CFs"]);
            Near("結果 JSON 主結構強度 = CFs ÷ AFu", r.Design.CFs / r.AFu, (double)json["structure"]["intensity"]);
            Equal("結果 JSON 尚無 BIM 明細", Newtonsoft.Json.Linq.JTokenType.Null, json["structure"]["bim"].Type);
            Near("結果 JSON 附輸入參數 SaD", 0.298, (double)json["inputs"]["project"]["SaD"]);

            Section("計算引擎：缺資料時的行為");
            var missing = QuantityInput.Load(samplePath);
            missing.Project.SaD = null;
            missing.Components.Add(new ComponentQuantity { Code = "XX-99", AreaM2 = 10, Name = "不存在的代號" });
            missing.Components.Add(new ComponentQuantity { Code = null, AreaM2 = 5, Name = "沒填代號的牆" });
            var m = calc.Calculate(missing);
            Equal("缺 SaD 時標示未完成", false, m.Complete);
            Equal("缺 SaD 時列出錯誤", true, m.Issues.Any(i => i.Level == IssueLevel.Error && i.Message.Contains("SaD")));
            Equal("找不到的代號與沒填代號都列入未對應", 2, m.Unmapped.Count);
        }

        private static void Within(string label, double expected, double actual, double tolerance)
        {
            Report(label, Math.Abs(expected - actual) <= tolerance, expected, actual);
        }

        /// <summary>和手冊數字的相對誤差在 0.05% 以內（手冊中間值有四捨五入與少數筆誤，見公式運算邏輯分頁）。</summary>
        private static void Relative(string label, double expected, double actual)
        {
            var ok = Math.Abs(expected - actual) <= Math.Abs(expected) * 0.0005;
            Report(label, ok, expected.ToString("N0"), actual.ToString("N0") + "（差 " + ((actual - expected) / expected).ToString("P3") + "）");
        }
    }
}
