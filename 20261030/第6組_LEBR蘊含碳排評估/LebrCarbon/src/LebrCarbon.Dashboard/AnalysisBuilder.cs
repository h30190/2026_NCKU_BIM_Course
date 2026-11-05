using LebrCarbon.Core.Lebr;
using LebrCarbon.Data;

namespace LebrCarbon.Dashboard;

/// <summary>
/// 快照的分析欄位（AI 分析計畫書 5.1 節）：哪裡佔比最多、每項還能降多少、距下一級還差多少。
/// 所有數字都由 LebrCalculator 算出；潛力是「只換這一項、其他不動」的單項試算。
/// </summary>
public sealed record SnapshotAnalysis
{
    public int Version { get; init; } = 1;
    public required StageShares StageShares { get; init; }
    public required List<Contribution> Contributions { get; init; }
    public required List<Potential> Potential { get; init; }
    /// <summary>已是最高級時為 null。</summary>
    public NextGrade? NextGrade { get; init; }
}

/// <summary>設計案地上層四階段（五段）各佔合計的比例。</summary>
public sealed record StageShares(double Structure, double NonStructural, double Renew, double Construction, double Demolition);

/// <summary>一列非主結構構件的碳排（新建 + 更新）與它的基準構件。</summary>
public sealed record Contribution
{
    /// <summary>構件在評估輸入中的序號，潛力與建議用它對應同一列。</summary>
    public required string Key { get; init; }
    public required string Category { get; init; }
    public required string CategoryName { get; init; }
    public string? BlccId { get; init; }
    public required string Name { get; init; }
    public string Source { get; init; } = "";
    public double Area { get; init; }
    public double DesignNew { get; init; }
    public double DesignRenew { get; init; }
    public double DesignTotal { get; init; }
    public string? BaselineBlccId { get; init; }
    public double? BaselineTotal { get; init; }
    /// <summary>佔非主結構（新建 + 更新）合計的比例。</summary>
    public double ShareOfNonStructural { get; init; }
    /// <summary>佔設計案地上層四階段合計的比例。</summary>
    public double ShareOfTotal { get; init; }
}

public sealed record PotentialOption(string BlccId, string Name, double Cfr, double DeltaCfr, string Grade, double DeltaEec);

/// <summary>
/// 一列構件換成同類別其他選項的單項試算，依 CFR 由高到低，只留前幾名。
/// NeedsRuleCheck：帷幕牆替換的結果受「帷幕牆基準案是否保留外裝」影響，規則確認前要標示。
/// </summary>
public sealed record Potential(string Key, string? BlccId, PotentialOption? Best, List<PotentialOption> Options, bool NeedsRuleCheck);

/// <summary>下一個較好的等級與門檻（CFR 須大於 CfrNeeded）。</summary>
public sealed record NextGrade(string Grade, double CfrNeeded, double Gap);

public static class AnalysisBuilder
{
    public const int MaxOptions = 10;

    public static SnapshotAnalysis Build(LebrProjectFile file, BlccLibrary lib, LebrResult r)
    {
        // ProjectEvaluator 依序保留面積 > 0 的構件，所以 r.DesignItems[j] 對應第 j 個面積 > 0 的輸入列
        var rows = file.DesignItems.Select((item, index) => (item, index)).Where(x => x.item.Area > 0).ToList();
        var nsTotal = r.CFns + r.CFrm;
        var total = r.Design.CFs + r.CFns + r.CFrm + r.CFc + r.CFdw;

        var contributions = r.DesignItems.Select((d, j) =>
        {
            var baseline = BaselineOf(d, lib);
            var designTotal = d.NewEmission + d.RenewEmission;
            return new Contribution
            {
                Key = rows[j].index.ToString(),
                Category = d.Category.Code(),
                CategoryName = d.Category.DisplayName(),
                BlccId = d.BlccId,
                Name = d.Name,
                Source = d.Source,
                Area = d.Area,
                DesignNew = d.NewEmission,
                DesignRenew = d.RenewEmission,
                DesignTotal = designTotal,
                BaselineBlccId = baseline?.Id,
                BaselineTotal = baseline is null ? null : d.Area * (baseline.New + baseline.Renew(r.Inputs.Wear)),
                ShareOfNonStructural = nsTotal > 0 ? designTotal / nsTotal : 0,
                ShareOfTotal = total > 0 ? designTotal / total : 0,
            };
        }).ToList();

        var potential = rows
            .Where(x => x.item.BlccId is not null && lib.ById.ContainsKey(x.item.BlccId))
            .Select(x => PotentialOf(file, lib, r, x.index, lib.ById[x.item.BlccId!]))
            .ToList();

        return new SnapshotAnalysis
        {
            StageShares = new StageShares(
                Share(r.Design.CFs, total), Share(r.CFns, total), Share(r.CFrm, total), Share(r.CFc, total), Share(r.CFdw, total)),
            Contributions = contributions,
            Potential = potential,
            NextGrade = NextGradeOf(r.CFR),
        };
    }

    public static NextGrade? NextGradeOf(double cfr)
    {
        // 門檻由好到差；比目前等級好一級的，就是最後一個「尚未超過」的門檻
        NextGrade? next = null;
        foreach (var (grade, min) in LebrTables.GradeThresholds)
        {
            if (cfr > min) break;
            next = new NextGrade(grade, min, min - cfr);
        }
        return next;
    }

    private static Potential PotentialOf(LebrProjectFile file, BlccLibrary lib, LebrResult r, int index, BlccItem current)
    {
        var options = lib.InCategory(current.CategoryEnum)
            .Where(b => !string.Equals(b.Id, current.Id, StringComparison.OrdinalIgnoreCase))
            .Select(b =>
            {
                var s = ScenarioRunner.RunEdited(file, lib, items => items[index] = items[index] with { BlccId = b.Id });
                return new PotentialOption(b.Id, b.Name, s.CFR, s.CFR - r.CFR, s.Grade, s.EEC - r.EEC);
            })
            .OrderByDescending(o => o.Cfr)
            .Take(MaxOptions)
            .ToList();

        var best = options.FirstOrDefault(o => o.DeltaCfr > 0);
        return new Potential(index.ToString(), current.Id, best, options,
            NeedsRuleCheck: current.CategoryEnum == NsCategory.CurtainWall);
    }

    private static BlccItem? BaselineOf(NonStructuralItem d, BlccLibrary lib)
    {
        var id = d.BlccId is not null && lib.ById.TryGetValue(d.BlccId, out var b) && b.BaselineId.Length > 0
            ? b.BaselineId
            : BaselineBuilder.DefaultBaselineId(d.Category);
        return id is not null && lib.ById.TryGetValue(id, out var baseline) ? baseline : null;
    }

    private static double Share(double part, double total) => total > 0 ? part / total : 0;
}
