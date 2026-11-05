using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// A8 維修門前作業淨空。作業區的幾何碰撞在 Revit 層（ShaftCollector）計算，
    /// 這裡只依碰撞結果判定，所以可以用假資料單元測試。
    /// </summary>
    public class A8_FrontClearance : RuleBase
    {
        public override string Id { get { return "A8"; } }
        public override string Name { get { return "門前作業淨空"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.HighRise246Space; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            foreach (var door in shaft.Doors)
            {
                if (!door.FrontCheckDone)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "維修門「" + door.Label + "」無法建立門前作業區（門缺少位置資訊）",
                        null, null, null, door.Id);
                    continue;
                }

                if (door.FrontObstructions.Count == 0) continue;

                string names = string.Join("、", door.FrontObstructions
                    .Select(o => (o.Category ?? "") + "「" + (o.Name ?? "") + "」" +
                                 (string.IsNullOrEmpty(o.Source) ? "" : "（連結：" + o.Source + "）"))
                    .Distinct());

                var ids = new List<long> { door.Id };
                // 連結模型中的障礙物 Id 不屬於主模型，不能用來上色或選取
                ids.AddRange(door.FrontObstructions.Where(o => string.IsNullOrEmpty(o.Source)).Select(o => o.Id));

                Issue issue = Make(shaft, Severity.Violation,
                    "維修門「" + door.Label + "」前方作業區（深 " + cfg.FrontClearanceDepthMm.ToString("F0") +
                    " mm、高 " + cfg.FrontClearanceHeightMm.ToString("F0") + " mm）內有：" + names,
                    door.FrontObstructions.Count, 0, "個", ids.ToArray());
                issue.Refs = new List<ElementRef> { new ElementRef(door.Id, shaft.SourceName) };
                issue.Refs.AddRange(door.FrontObstructions.Select(o => new ElementRef(o.Id, string.IsNullOrEmpty(o.Source) ? null : o.Source)));
                yield return issue;
            }
        }
    }
}
