using System.Collections.Generic;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// A4 維修門不得開向樓梯間或排煙室。門另一側的房間由 Revit 依階段取得
    /// （door.get_FromRoom(phase) / get_ToRoom(phase)），房間名稱或「空間類型」參數任一符合即判定違規。
    /// </summary>
    public class A4_DoorNotToStair : RuleBase
    {
        public override string Id { get { return "A4"; } }
        public override string Name { get { return "維修門不得開向樓梯間或排煙室"; } }
        public override RuleNature Nature { get { return RuleNature.Regulation; } }
        public override string Reference { get { return LegalRefs.Stair97; } }
        protected override string UndergroundReference { get { return LegalRefs.Stair97Underground; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            foreach (var door in shaft.Doors)
            {
                bool byName = RuleConfig.MatchesAny(door.OtherSideRoomName, cfg.StairRoomPatterns);
                bool byType = RuleConfig.MatchesAny(door.OtherSideRoomType, cfg.StairRoomPatterns);
                if (!byName && !byType) continue;

                var ids = new List<long> { door.Id };
                if (door.OtherSideRoomId.HasValue) ids.Add(door.OtherSideRoomId.Value);

                string target = "「" + (door.OtherSideRoomName ?? "（未命名）") + "」" +
                                (byType ? "（空間類型＝" + door.OtherSideRoomType + "）" : "");
                yield return Make(shaft, Severity.Violation,
                    "維修門「" + door.Label + "」開向" + target,
                    "將維修門改開向走廊、梯廳或機房等一般空間；若無法改向，需調整管道間位置",
                    null, null, null, ids.ToArray());
            }
        }
    }
}
