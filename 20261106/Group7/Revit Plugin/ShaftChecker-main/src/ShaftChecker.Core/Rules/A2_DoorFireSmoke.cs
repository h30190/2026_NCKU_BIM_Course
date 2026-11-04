using System.Collections.Generic;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>A2 維修門防火時效 ≥ 1 小時並具遮煙性能（設計施工編 第79-2條）。</summary>
    public class A2_DoorFireSmoke : RuleBase
    {
        public override string Id { get { return "A2"; } }
        public override string Name { get { return "維修門防火時效與遮煙"; } }
        public override RuleNature Nature { get { return RuleNature.Regulation; } }
        public override string Reference { get { return LegalRefs.Shaft79_2; } }
        protected override string UndergroundReference { get { return LegalRefs.Underground203; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (shaft.Doors.Count == 0)
            {
                yield return Make(shaft, Severity.Warning,
                    "找不到開向此管道間的維修門（請確認門的 FromRoom/ToRoom 是否為此管道間）");
                yield break;
            }

            foreach (var door in shaft.Doors)
            {
                double? hours = FireRatingParser.ParseHours(door.FireRatingRaw);
                if (!hours.HasValue)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "維修門「" + door.Label + "」未填防火時效",
                        null, cfg.MinFireRatingHours, "hr", door.Id);
                }
                else if (hours.Value < cfg.MinFireRatingHours)
                {
                    yield return Make(shaft, Severity.Violation,
                        "維修門「" + door.Label + "」防火時效 " + door.FireRatingRaw +
                        "，未達 " + cfg.MinFireRatingHours + " 小時",
                        hours.Value, cfg.MinFireRatingHours, "hr", door.Id);
                }

                if (!door.SmokeSeal.HasValue)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "維修門「" + door.Label + "」未填「" + cfg.SmokeSealParam + "」參數",
                        null, null, null, door.Id);
                }
                else if (!door.SmokeSeal.Value)
                {
                    yield return Make(shaft, Severity.Violation,
                        "維修門「" + door.Label + "」不具遮煙性能",
                        null, null, null, door.Id);
                }
            }
        }
    }
}
