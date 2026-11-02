using System;
using LebrCarbon;

namespace LebrDatabaseTests
{
    /// <summary>專案設定表單規則的測試（CSER、LL 上限與構造限制）。</summary>
    internal static partial class Program
    {
        private static void TestBaselineIndependentOfDesignSpF(LebrDatabase db, string samplePath)
        {
            var calc = new LebrCalculator(db);
            var full = calc.Calculate(QuantityInput.Load(samplePath));

            var input = QuantityInput.Load(samplePath);
            input.Building.Sp = null;
            input.Building.F = null;
            var partial = calc.Calculate(input);

            Section("引擎：基準案不受設計案 Sp、F 缺少影響");
            Equal("設計案缺 Sp、F 時 Complete = false", false, partial.Complete);
            Near("設計案缺 Sp、F 時設計案主結構 CFs = 0", 0, partial.Design.CFs);
            Relative("設計案缺 Sp、F 時基準案主結構 CFsc 仍相同", full.Baseline.CFs, partial.Baseline.CFs);
            Near("基準案 EBF = 0，Rn = 1.0", 1.0, partial.Structure.BaselineRn);
        }

        private static void TestProjectSettingsRules(LebrDatabase db)
        {
            var table = db.Parameters.LifeExtensionLL;

            Section("專案設定：CSER 構造條件");
            Equal("RC 可勾選 CSER", true, ProjectSettingsRules.CanUseCser("RC"));
            Equal("SRC 可勾選 CSER", true, ProjectSettingsRules.CanUseCser("SRC"));
            Equal("S 不可勾選 CSER", false, ProjectSettingsRules.CanUseCser("S"));
            Equal("wood 不可勾選 CSER", false, ProjectSettingsRules.CanUseCser("wood"));
            Near("未勾選 CSER 寫入 0", 0, ProjectSettingsRules.CserValue(false, 1.7));
            Near("勾選 CSER 寫入輸入值", 1.7, ProjectSettingsRules.CserValue(true, 1.7));
            Near("勾選但未輸入 CSER 寫入 0", 0, ProjectSettingsRules.CserValue(true, null));

            Section("專案設定：LL 構造與上限");
            Equal("RC 可選項目 1", true, ProjectSettingsRules.CanUseRcOnlyLl("RC"));
            Equal("SRC 不可選項目 1、2", false, ProjectSettingsRules.CanUseRcOnlyLl("SRC"));
            Near("RC 項目 1+3 超過上限，以 0.08 計", 0.08, ProjectSettingsRules.ComputeLl(table, "RC", 1, 3, true));
            Near("SRC 項目 1 不計，只剩項目 3", 0.05, ProjectSettingsRules.ComputeLl(table, "SRC", 1, 3, true));
            Near("RC 只選項目 2", 0.02, ProjectSettingsRules.ComputeLl(table, "RC", 2, null, true));
            Near("RC 只選項目 4", 0.04, ProjectSettingsRules.ComputeLl(table, "S", null, 4, true));
            Near("未附證明文件 LL = 0", 0, ProjectSettingsRules.ComputeLl(table, "RC", 1, 3, false));
            Near("未選任何項目 LL = 0", 0, ProjectSettingsRules.ComputeLl(table, "RC", null, null, true));

            Section("專案設定：必填檢查");
            var m = new ProjectSettingsModel();
            Equal("未估算且全部缺少時列出 5 項", 5, ProjectSettingsRules.MissingRequired(m).Count);
            m.Estimate = true;
            Equal("估算勾選時不檢查必填", 0, ProjectSettingsRules.MissingRequired(m).Count);

            Section("專案設定：構造變更後清除不適用的選項");
            var s = new ProjectSettingsModel { Structure = "RC", CserEnabled = true, DurableItemId = 1, QualityItemId = 3 };
            s.Structure = "SRC";
            s.ApplyStructureRules(table);
            Equal("SRC 保留 CSER 勾選", true, s.CserEnabled);
            Equal("SRC 清除耐久設計項目 1", true, !s.DurableItemId.HasValue);
            Equal("SRC 保留高品質施工項目 3", 3, s.QualityItemId.Value);
            s.Structure = "S";
            s.ApplyStructureRules(table);
            Equal("S 清除 CSER 勾選", false, s.CserEnabled);
        }
    }
}
