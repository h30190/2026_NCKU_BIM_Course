using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>依設定檔執行規則，並為每個管道間彙整結果。</summary>
    public class RuleEngine
    {
        /// <summary>模組一的全部規則。新增規則時加在這裡。</summary>
        public static readonly IShaftRule[] AllRules =
        {
            new A1_WallFireRating(),
            new A2_DoorFireSmoke(),
            new A4_DoorNotToStair(),
            new A6_ClearSize(),
            new A7_DoorSize(),
            new A8_FrontClearance(),
            new A9_DoorHeadBeam(),
            new P1_WaterElectricSeparation(),
            new P2_PipeClearance(),
            new P3_DoorBackClearance(),
            new P4_GasShaft(),
            new P5_FireDamper(),
            new S1_ShaftStructure(),
            new S2_PipeThroughBeam()
        };

        public CheckResult Run(IEnumerable<ShaftInfo> shafts, RuleConfig cfg)
        {
            var result = new CheckResult();
            var rules = AllRules.Where(r => cfg.IsEnabled(r.Id)).ToList();

            foreach (var shaft in shafts)
            {
                shaft.EnsureMetrics();
                var sr = new ShaftResult { Shaft = shaft };

                foreach (var rule in rules)
                {
                    // An empty scan is not evidence of compliance. Preserve unrelated
                    // architectural checks while making incomplete MEP scans explicit.
                    bool scanRequired = rule.Group == RuleGroups.Mep || rule.Group == RuleGroups.Structure || rule.Id == "A9";
                    if ((scanRequired && !shaft.ScanComplete) || (rule.Group == RuleGroups.Mep && !shaft.MepModelFound))
                    {
                        sr.Issues.Add(new Issue
                        {
                            RuleId = rule.Id, RuleName = rule.Name, Nature = rule.Nature,
                            Reference = rule.ReferenceFor(shaft), ShaftRoomId = shaft.RoomId,
                            Severity = Severity.DataMissing,
                            Message = !shaft.ScanComplete ? "機電／結構資料擷取未完成，無法判定此規則"
                                : "專案（含已載入連結）未找到機電管線，無法判定此規則",
                            Suggestion = "確認所需模型已連結及載入，補齊資料後重新檢查"
                        });
                        continue;
                    }
                    var issues = rule.Check(shaft, cfg).ToList();
                    if (issues.Count == 0)
                    {
                        // 合格也記一筆，報表才看得到「檢討過、而且合格」
                        issues.Add(new Issue
                        {
                            RuleId = rule.Id,
                            RuleName = rule.Name,
                            Nature = rule.Nature,
                            Reference = rule.ReferenceFor(shaft),
                            ShaftRoomId = shaft.RoomId,
                            Severity = Severity.Pass,
                            Message = "合格"
                        });
                    }
                    sr.Issues.AddRange(issues);
                }

                sr.Overall = sr.Issues.Count == 0 ? Severity.Pass : sr.Issues.Max(i => i.Severity);
                result.Shafts.Add(sr);
            }

            return result;
        }
    }
}
