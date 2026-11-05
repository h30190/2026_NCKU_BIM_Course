using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// P4 瓦斯管獨立：高層建築物中，瓦斯管與允許系統（預設：給排水）以外的管線同在一個管道間時判定違規。
    /// 法條原文為「瓦斯管之管道間應單獨設置」；要完全單獨時，把設定檔 gasAllowedGroups 設為空清單。
    /// </summary>
    public class P4_GasShaft : RuleBase
    {
        public override string Id { get { return "P4"; } }
        public override string Name { get { return "瓦斯管道間獨立"; } }
        public override RuleNature Nature { get { return RuleNature.Regulation; } }
        public override string Reference { get { return LegalRefs.HighRise246Gas; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (!shaft.IsHighRise) yield break;

            var inShaft = shaft.Mep.Where(m => ShaftSpace.IsInShaft(shaft, m)).ToList();
            var gas = inShaft.Where(m => m.Group == MepGroup.Gas).ToList();
            if (gas.Count == 0) yield break;

            var allowed = MepClassifier.GasAllowed(cfg);
            var others = inShaft.Where(m => !allowed.Contains(m.Group)).ToList();
            if (others.Count == 0) yield break;

            var ids = new List<object>();
            ids.AddRange(gas.Take(3));
            ids.AddRange(others.Take(5));
            string allowText = allowed.Count <= 1 ? "其他任何系統" : "給排水以外的系統";
            yield return MakeFor(shaft, Severity.Violation,
                "高層建築物的瓦斯管（" + gas.Count + " 條）與" + allowText + "同在一個管道間：" + MepClassifier.Summarize(others),
                "瓦斯管改設獨立管道間，或將其他系統移出本管道間",
                others.Count, 0, "條", ids.ToArray());
        }
    }
}
