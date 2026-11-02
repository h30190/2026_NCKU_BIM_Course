using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;
using LebrCarbon;

namespace LebrCarbon.Revit
{
    /// <summary>專案設定表單與專案資訊 LEBR_* 參數之間的讀寫。數字為 0 或空白視為未填。</summary>
    public static class ProjectSettingsStore
    {
        private static readonly string[] Names =
        {
            "LEBR_WearClass", "LEBR_UseType", "LEBR_Structure",
            "LEBR_I", "LEBR_SaD", "LEBR_Fu", "LEBR_L", "LEBR_D0", "LEBR_Rs",
            "LEBR_CSER", "LEBR_LL", "LEBR_LL_Durable", "LEBR_LL_Quality",
            "LEBR_S", "LEBR_Sp", "LEBR_F", "LEBR_AFu", "LEBR_CFs", "LEBR_EstimateJson",
        };

        /// <summary>專案資訊缺少的 LEBR 參數（尚未建立）。</summary>
        public static IList<string> MissingParameters(Document doc)
        {
            var info = doc.ProjectInformation;
            return Names.Where(n => info.LookupParameter(n) == null).ToList();
        }

        public static ProjectSettingsModel Read(Document doc, LifeExtensionTable table)
        {
            var info = doc.ProjectInformation;
            var cser = Number(info, "LEBR_CSER");
            var s = Number(info, "LEBR_S");
            var sp = Number(info, "LEBR_Sp");
            var f = Number(info, "LEBR_F");
            var afu = Number(info, "LEBR_AFu");
            return new ProjectSettingsModel
            {
                WearClass = Text(info, "LEBR_WearClass"),
                UseType = Text(info, "LEBR_UseType"),
                Structure = Text(info, "LEBR_Structure"),
                I = Number(info, "LEBR_I"),
                SaD = Number(info, "LEBR_SaD"),
                Fu = Number(info, "LEBR_Fu"),
                L = Number(info, "LEBR_L"),
                D0 = Number(info, "LEBR_D0"),
                Rs = Number(info, "LEBR_Rs") ?? 0.95,
                CserEnabled = cser.HasValue,
                CserInput = cser,
                StructureOverrideEnabled = s.HasValue || sp.HasValue || f.HasValue,
                OverrideS = s,
                OverrideSp = sp,
                OverrideF = f,
                AreaOverrideEnabled = afu.HasValue,
                OverrideAFu = afu,
                DurableItemId = ToItemId(Number(info, "LEBR_LL_Durable")),
                QualityItemId = ToItemId(Number(info, "LEBR_LL_Quality")),
                ProofAttached = (Number(info, "LEBR_LL") ?? 0) > 0,
                EstimateCfs = Number(info, "LEBR_CFs"),
                EstimateSummary = EstimateSummaryOf(Text(info, "LEBR_EstimateJson")),
            };
        }

        private static string EstimateSummaryOf(string json)
        {
            double above, below, cfs;
            if (!EstimateRecord.TryReadSummary(json, out above, out below, out cfs)) return null;
            return "地上 " + above.ToString("N0") + "　地下 " + below.ToString("N0") + "　合計 " + cfs.ToString("N0") + " kgCO2e";
        }

        /// <summary>寫回專案資訊。LL 寫入已限制上限的合計值；項目編號與合計值一併寫入，供下次還原選單。</summary>
        public static void Write(Document doc, ProjectSettingsModel m, double ll)
        {
            var info = doc.ProjectInformation;
            using (var t = new Transaction(doc, "LEBR 專案設定"))
            {
                t.Start();
                SetText(info, "LEBR_WearClass", m.WearClass);
                SetText(info, "LEBR_UseType", m.UseType);
                SetText(info, "LEBR_Structure", m.Structure);
                SetNumber(info, "LEBR_I", m.I);
                SetNumber(info, "LEBR_SaD", m.SaD);
                SetNumber(info, "LEBR_Fu", m.Fu);
                SetNumber(info, "LEBR_L", m.L);
                SetNumber(info, "LEBR_D0", m.D0);
                SetNumber(info, "LEBR_Rs", m.Rs);
                SetNumber(info, "LEBR_CSER", ProjectSettingsRules.CserValue(m.CserEnabled, m.CserInput));
                SetNumber(info, "LEBR_LL", ll);
                SetNumber(info, "LEBR_LL_Durable", m.DurableItemId.HasValue ? (double?)m.DurableItemId.Value : null);
                SetNumber(info, "LEBR_LL_Quality", m.QualityItemId.HasValue ? (double?)m.QualityItemId.Value : null);
                SetNumber(info, "LEBR_S", m.OverrideS);
                SetNumber(info, "LEBR_Sp", m.OverrideSp);
                SetNumber(info, "LEBR_F", m.OverrideF);
                SetNumber(info, "LEBR_AFu", m.OverrideAFu);
                if (!m.Estimate)
                {
                    SetNumber(info, "LEBR_CFs", null);
                    SetText(info, "LEBR_EstimateJson", null);
                }
                t.Commit();
            }
        }

        /// <summary>寫入估算結果（LEBR_CFs 與 LEBR_EstimateJson）。由估算視窗在「計算並儲存」時呼叫。</summary>
        public static void WriteEstimate(Document doc, double cfs, string estimateJson)
        {
            var info = doc.ProjectInformation;
            using (var t = new Transaction(doc, "LEBR 結構碳排估算"))
            {
                t.Start();
                SetNumber(info, "LEBR_CFs", cfs);
                SetText(info, "LEBR_EstimateJson", estimateJson);
                t.Commit();
            }
        }

        // ------------------------------------------------------------ 小工具

        private static int? ToItemId(double? value)
        {
            return value.HasValue ? (int?)Math.Round(value.Value) : null;
        }

        private static string Text(Element e, string name)
        {
            var p = e.LookupParameter(name);
            return p == null || !p.HasValue ? null : p.AsString();
        }

        private static double? Number(Element e, string name)
        {
            var p = e.LookupParameter(name);
            if (p == null || !p.HasValue) return null;
            double v;
            if (p.StorageType == StorageType.Double) v = p.AsDouble();
            else if (p.StorageType == StorageType.Integer) v = p.AsInteger();
            else if (p.StorageType != StorageType.String || !double.TryParse(p.AsString(), out v)) return null;
            return Math.Abs(v) > 1e-12 ? v : (double?)null;
        }

        private static void SetText(Element e, string name, string value)
        {
            Require(e, name).Set(value ?? "");
        }

        private static void SetNumber(Element e, string name, double? value)
        {
            Require(e, name).Set(value ?? 0);
        }

        private static Parameter Require(Element e, string name)
        {
            var p = e.LookupParameter(name);
            if (p == null) throw new InvalidOperationException("找不到專案參數 " + name + "，請重新開啟「專案設定」（會自動建立參數）。");
            return p;
        }
    }
}
