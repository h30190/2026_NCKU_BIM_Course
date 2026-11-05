using System.Collections.Generic;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>A1 管道間邊界牆防火時效 ≥ 1 小時（設計施工編 第79-2條）。</summary>
    public class A1_WallFireRating : RuleBase
    {
        public override string Id { get { return "A1"; } }
        public override string Name { get { return "管道間牆體防火時效"; } }
        public override RuleNature Nature { get { return RuleNature.Regulation; } }
        public override string Reference { get { return LegalRefs.Shaft79_2; } }
        protected override string UndergroundReference { get { return LegalRefs.Underground203; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (shaft.Walls.Count == 0)
            {
                yield return Make(shaft, Severity.DataMissing,
                    "找不到管道間的邊界牆（邊界可能由房間分隔線或柱構成）");
                yield break;
            }

            foreach (var wall in shaft.Walls)
            {
                double? hours = FireRatingParser.ParseHours(wall.FireRatingRaw);
                if (!hours.HasValue)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "牆類型「" + wall.TypeName + "」未填防火時效",
                        null, cfg.MinFireRatingHours, "hr", wall.Id);
                }
                else if (hours.Value < cfg.MinFireRatingHours)
                {
                    yield return Make(shaft, Severity.Violation,
                        "牆類型「" + wall.TypeName + "」防火時效 " + wall.FireRatingRaw +
                        "，未達 " + cfg.MinFireRatingHours + " 小時",
                        hours.Value, cfg.MinFireRatingHours, "hr", wall.Id);
                }
            }
        }
    }
}
