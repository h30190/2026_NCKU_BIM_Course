using System.Collections.Generic;
using System.Linq;
using ShaftChecker.Core.Analysis;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Geometry;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// A9 門頂梁下淨高：找出投影落在門洞上的梁，梁底到門檻的淨高須 ≥ 門高 + 門框預留。
    /// 建築人員多在建築平面作業、少開結構視圖，梁從門頂上方經過時常被忽略。
    /// </summary>
    public class A9_DoorHeadBeam : RuleBase
    {
        public override string Id { get { return "A9"; } }
        public override string Name { get { return "門頂梁下淨高"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.HighRise246Space; } }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            if (shaft.Doors.Count == 0) yield break;
            if (!shaft.StructureModelFound)
            {
                yield return Make(shaft, Severity.Warning,
                    "專案（含連結）中找不到結構梁，無法檢討門頂梁下淨高；請確認結構模型已連結並載入");
                yield break;
            }
            if (shaft.Beams.Count == 0) yield break; // 附近沒有梁

            foreach (DoorInfo d in shaft.Doors)
            {
                if (!d.HasGeometry)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "維修門「" + d.Label + "」無法取得位置，不能檢討門頂梁下淨高", null, null, null, d.Id);
                    continue;
                }

                double doorH = d.HeightMm > 0 ? d.HeightMm : cfg.MinDoorHeightMm;
                double required = doorH + cfg.DoorHeadAllowanceMm;
                var opening = ShaftSpace.DoorOpening(d);

                BeamInfo lowest = null;
                foreach (BeamInfo b in shaft.Beams)
                {
                    if (b.WidthMm <= 0 || b.BottomZ <= d.BaseZ + 300) continue;          // 樓板下的梁或地樑
                    if (ShaftSpace.HasStorey(shaft) && b.BottomZ > shaft.CeilingZ + 50) continue;
                    if (!PolygonUtils.PolygonsIntersect(b.Footprint(), opening)) continue;
                    if (lowest == null || b.BottomZ < lowest.BottomZ) lowest = b;
                }
                if (lowest == null) continue;

                double clear = lowest.BottomZ - d.BaseZ;
                if (clear >= required) continue;

                string suggestion = clear >= cfg.MinDoorHeightMm + cfg.DoorHeadAllowanceMm
                    ? "改用高度 ≤ " + (clear - cfg.DoorHeadAllowanceMm).ToString("N0") + " mm 的門，或將門沿牆移出梁寬範圍"
                    : "梁下淨高不足以設置 " + cfg.MinDoorHeightMm.ToString("N0") + " mm 高的維修門：請將門沿牆移出梁的投影範圍，或與結構協調梁位";
                yield return MakeFor(shaft, Severity.Violation,
                    "維修門「" + d.Label + "」上方有" + lowest.Label + "，梁底至門檻淨高 " + clear.ToString("N0") +
                    " mm，小於門高 " + doorH.ToString("N0") + " mm + 預留 " + cfg.DoorHeadAllowanceMm.ToString("N0") + " mm",
                    suggestion, clear, required, "mm", d, lowest);
            }
        }
    }
}
