using System;
using System.Collections.Generic;
using System.Linq;

namespace LebrCarbon
{
    /// <summary>專案設定表單的規則。不依賴 Revit API，可在 Revit 外測試。</summary>
    public static class ProjectSettingsRules
    {
        /// <summary>CSER 只適用 RC 或 SRC 構造（表單 A-2）。</summary>
        public static bool CanUseCser(string structure)
        {
            return structure == "RC" || structure == "SRC";
        }

        /// <summary>表 5-2 標示 rc_only 的項目只適用 RC 構造（SRC 不可選）。</summary>
        public static bool CanUseRcOnlyLl(string structure)
        {
            return structure == "RC";
        }

        /// <summary>CSER 寫入值：未勾選為 0，勾選後為輸入值。</summary>
        public static double CserValue(bool enabled, double? input)
        {
            return enabled && input.HasValue ? input.Value : 0;
        }

        /// <summary>
        /// LL 合計：耐久設計與高品質施工各擇一，兩組可累加；
        /// 無證明文件為 0；合計超過 max_total 時以上限計算。
        /// </summary>
        public static double ComputeLl(LifeExtensionTable table, string structure, int? durableItemId, int? qualityItemId, bool proofAttached)
        {
            if (table == null || !proofAttached) return 0;
            double total = 0;
            foreach (var id in new[] { durableItemId, qualityItemId })
            {
                if (!id.HasValue) continue;
                var item = table.Items.FirstOrDefault(i => i.Id == id.Value);
                if (item == null) continue;
                if (item.RcOnly && !CanUseRcOnlyLl(structure)) continue;
                total += item.LL;
            }
            return Math.Min(total, table.MaxTotal);
        }

        /// <summary>未勾選「使用估算」時，必填的主結構欄位缺少者。</summary>
        public static IList<string> MissingRequired(ProjectSettingsModel m)
        {
            var missing = new List<string>();
            if (m.Estimate) return missing;
            if (!m.I.HasValue) missing.Add("I");
            if (!m.SaD.HasValue) missing.Add("SaD");
            if (!m.Fu.HasValue) missing.Add("Fu");
            if (!m.L.HasValue) missing.Add("L");
            if (!m.D0.HasValue) missing.Add("D0");
            return missing;
        }
    }

    /// <summary>專案設定表單的資料。欄位沒填時為 null。</summary>
    public class ProjectSettingsModel
    {
        public string WearClass { get; set; }
        public string Structure { get; set; }
        public string UseType { get; set; }

        /// <summary>使用估算：A 區鎖定，本次只做 UI，不接估算程式。</summary>
        public bool Estimate { get; set; }

        public double? I { get; set; }
        public double? SaD { get; set; }
        public double? Fu { get; set; }
        public double? L { get; set; }
        public double? D0 { get; set; }
        public double Rs { get; set; } = 0.95;

        public bool CserEnabled { get; set; }
        public double? CserInput { get; set; }

        /// <summary>A-2 覆寫：S 同步套用基準案；Sp、F 僅設計案。</summary>
        public bool StructureOverrideEnabled { get; set; }
        public double? OverrideS { get; set; }
        public double? OverrideSp { get; set; }
        public double? OverrideF { get; set; }

        /// <summary>B 區覆寫：AFu 設計案與基準案相同。</summary>
        public bool AreaOverrideEnabled { get; set; }
        public double? OverrideAFu { get; set; }

        /// <summary>結構碳排估算值（LEBR_CFs），只用於畫面顯示；未估算時為 null。</summary>
        public double? EstimateCfs { get; set; }

        /// <summary>估算摘要文字（地上、地下、合計），只用於畫面顯示。</summary>
        public string EstimateSummary { get; set; }

        public int? DurableItemId { get; set; }
        public int? QualityItemId { get; set; }
        public bool ProofAttached { get; set; }

        /// <summary>依構造與表 5-2 清除不適用的設定。</summary>
        public void ApplyStructureRules(LifeExtensionTable table)
        {
            if (!ProjectSettingsRules.CanUseCser(Structure))
                CserEnabled = false;
            if (!ProjectSettingsRules.CanUseRcOnlyLl(Structure) && table != null)
            {
                if (IsRcOnly(table, DurableItemId)) DurableItemId = null;
                if (IsRcOnly(table, QualityItemId)) QualityItemId = null;
            }
        }

        /// <summary>取消勾選覆寫時清空已填的值。</summary>
        public void ClearOverridesIfDisabled()
        {
            if (!StructureOverrideEnabled)
            {
                OverrideS = null;
                OverrideSp = null;
                OverrideF = null;
            }
            if (!AreaOverrideEnabled) OverrideAFu = null;
        }

        private static bool IsRcOnly(LifeExtensionTable table, int? id)
        {
            if (!id.HasValue) return false;
            var item = table.Items.FirstOrDefault(i => i.Id == id.Value);
            return item != null && item.RcOnly;
        }
    }
}
