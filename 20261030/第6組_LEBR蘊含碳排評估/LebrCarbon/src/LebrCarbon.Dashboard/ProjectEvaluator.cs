using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>
/// 不開 Revit，直接以 .lebr.json 的輸入重算 LEBR（與評估視窗「計算」相同步驟）。
/// 供命令列工具與教室 demo 使用；公式仍只在 LebrCalculator。
/// </summary>
public static class ProjectEvaluator
{
    public static LebrResult Evaluate(LebrProjectFile file, BlccLibrary lib)
    {
        var p = file.Inputs;
        p.F = Math.Round(file.Shape.F, 4);

        // 與評估視窗相同：有 B-LCC ID 的構件依目前耗損等級重新帶入碳排標準，面積為 0 的不計
        var design = file.DesignItems
            .Select(i => i.BlccId is not null && lib.ById.TryGetValue(i.BlccId, out var b)
                ? i with { Category = b.CategoryEnum, Name = b.Name, NewEf = b.New, RenewEf = b.Renew(p.Wear) }
                : i)
            .Where(i => i.Area > 0)
            .ToList();

        // 空清單代表基準案由 B-LCC 自動產生（LebrProjectFile.BaselineItems 說明）
        var baseline = file.BaselineItems.Count > 0
            ? file.BaselineItems.Where(i => i.Area > 0).ToList()
            : BaselineBuilder.Build(design, lib.ById, p.Wear);

        return LebrCalculator.Evaluate(p, design, baseline);
    }
}
