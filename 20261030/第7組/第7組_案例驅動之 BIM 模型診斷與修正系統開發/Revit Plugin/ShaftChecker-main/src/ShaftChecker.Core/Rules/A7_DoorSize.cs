using System.Collections.Generic;
using ShaftChecker.Core.Config;
using ShaftChecker.Core.Models;

namespace ShaftChecker.Core.Rules
{
    /// <summary>
    /// A7 維修門淨寬、淨高。優先讀門的「淨寬」「淨高」參數；沒有時以寬度、高度扣除門框寬度推算
    /// （有「門框寬度」參數：淨寬 = 寬度 − 2 × 框寬、淨高 = 高度 − 框寬；沒有則扣設定檔的扣除值）。
    /// </summary>
    public class A7_DoorSize : RuleBase
    {
        public override string Id { get { return "A7"; } }
        public override string Name { get { return "維修門尺寸"; } }
        public override RuleNature Nature { get { return RuleNature.DesignParameter; } }
        public override string Reference { get { return LegalRefs.HighRise246Space; } }

        /// <summary>推算淨寬；回傳值與說明（例如「寬度 800 − 2 × 框寬 40」）。</summary>
        public static double ClearWidth(DoorInfo d, RuleConfig cfg, out string how)
        {
            if (d.ClearWidthMm.HasValue) { how = "淨寬參數"; return d.ClearWidthMm.Value; }
            if (d.FrameWidthMm.HasValue)
            {
                how = "寬度 " + d.WidthMm.ToString("F0") + " mm − 2 × 門框 " + d.FrameWidthMm.Value.ToString("F0") + " mm";
                return d.WidthMm - 2 * d.FrameWidthMm.Value;
            }
            how = cfg.DoorFrameDeductionMm > 0
                ? "寬度 " + d.WidthMm.ToString("F0") + " mm − 門框扣除 " + cfg.DoorFrameDeductionMm.ToString("F0") + " mm"
                : "寬度（未扣門框）";
            return d.WidthMm - cfg.DoorFrameDeductionMm;
        }

        public static double ClearHeight(DoorInfo d, RuleConfig cfg, out string how)
        {
            if (d.ClearHeightMm.HasValue) { how = "淨高參數"; return d.ClearHeightMm.Value; }
            if (d.FrameWidthMm.HasValue)
            {
                how = "高度 " + d.HeightMm.ToString("F0") + " mm − 門框 " + d.FrameWidthMm.Value.ToString("F0") + " mm";
                return d.HeightMm - d.FrameWidthMm.Value;
            }
            how = cfg.DoorFrameHeadDeductionMm > 0
                ? "高度 " + d.HeightMm.ToString("F0") + " mm − 門框扣除 " + cfg.DoorFrameHeadDeductionMm.ToString("F0") + " mm"
                : "高度（未扣門框）";
            return d.HeightMm - cfg.DoorFrameHeadDeductionMm;
        }

        public override IEnumerable<Issue> Check(ShaftInfo shaft, RuleConfig cfg)
        {
            foreach (var door in shaft.Doors)
            {
                if (door.WidthMm <= 0 && !door.ClearWidthMm.HasValue)
                {
                    yield return Make(shaft, Severity.DataMissing,
                        "維修門「" + door.Label + "」讀不到寬度或淨寬參數", null, null, null, door.Id);
                }
                else
                {
                    string how;
                    double clear = ClearWidth(door, cfg, out how);
                    if (clear < cfg.MinDoorClearWidthMm)
                    {
                        yield return Make(shaft, Severity.Violation,
                            "維修門「" + door.Label + "」淨寬 " + clear.ToString("F0") + " mm（" + how + "），小於 " +
                            cfg.MinDoorClearWidthMm.ToString("F0") + " mm",
                            "更換為淨寬至少 " + cfg.MinDoorClearWidthMm.ToString("F0") + " mm 的門",
                            clear, cfg.MinDoorClearWidthMm, "mm", door.Id);
                    }
                }

                if (door.HeightMm <= 0 && !door.ClearHeightMm.HasValue) continue;

                string howH;
                double clearH = ClearHeight(door, cfg, out howH);
                if (clearH < cfg.MinDoorHeightMm)
                {
                    yield return Make(shaft, Severity.Violation,
                        "維修門「" + door.Label + "」淨高 " + clearH.ToString("F0") + " mm（" + howH + "），小於 " +
                        cfg.MinDoorHeightMm.ToString("F0") + " mm",
                        "更換為淨高至少 " + cfg.MinDoorHeightMm.ToString("F0") + " mm 的門（並確認門頂上方梁下淨高，見 A9）",
                        clearH, cfg.MinDoorHeightMm, "mm", door.Id);
                }
            }
        }
    }
}
